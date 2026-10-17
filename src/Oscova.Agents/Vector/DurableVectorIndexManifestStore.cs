using Oscova.Agents.Secrets;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Vector;

/// <summary>Reads and atomically writes one durable vector-index manifest.</summary>
internal sealed class DurableVectorIndexManifestStore(
    string manifestPath,
    ILogger logger,
    IContentProtector protector,
    string userId,
    string purpose)
{
    private int _malformedWarningReported;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<DurableVectorIndexManifestLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            return new DurableVectorIndexManifestLoadResult(new DurableVectorIndexManifest(), false, false);
        }

        try
        {
            // Read a complete snapshot while allowing the indexer to publish its replacement.
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string ciphertext = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string json = protector.Unprotect(userId, purpose, ciphertext)
                ?? throw new InvalidDataException("The encrypted vector manifest could not be authenticated.");
            DurableVectorIndexManifest? manifest =
                JsonSerializer.Deserialize<DurableVectorIndexManifest>(json, JsonOptions);
            if (manifest is null)
            {
                throw new JsonException("The durable vector-index manifest was empty.");
            }

            manifest.Sources = new Dictionary<string, DurableVectorSourceEntry>(
                manifest.Sources ?? [],
                DurableVectorIndexManifest.SourceComparer);
            if (!IsValid(manifest))
            {
                throw new JsonException("The durable vector-index manifest contained invalid state.");
            }

            // A trusted read ends the current malformed episode so later corruption is visible once.
            Interlocked.Exchange(ref _malformedWarningReported, 0);
            return new DurableVectorIndexManifestLoadResult(manifest, true, false);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            if (Interlocked.Exchange(ref _malformedWarningReported, 1) == 0)
            {
                logger.LogWarning(ex, "The durable vector-index manifest is malformed and will be rebuilt.");
            }

            return new DurableVectorIndexManifestLoadResult(new DurableVectorIndexManifest(), true, true);
        }
    }

    public async Task SaveAsync(
        DurableVectorIndexManifest manifest,
        CancellationToken cancellationToken = default)
    {
        manifest.Version = DurableVectorIndexManifest.CurrentVersion;
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        string ciphertext = protector.Protect(userId, purpose, json);
        await FileUtil.WriteAllTextAtomicAsync(manifestPath, ciphertext, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _malformedWarningReported, 0);
    }

    private static bool IsValid(DurableVectorIndexManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Fingerprint) || manifest.VectorDimensions <= 0)
        {
            return false;
        }

        foreach ((string sourceId, DurableVectorSourceEntry? source) in manifest.Sources)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || source is null ||
                !Enum.IsDefined(source.Mode) || !Enum.IsDefined(source.State) ||
                source.CommittedPosition < 0)
            {
                return false;
            }

            if (source.Mode == DurableVectorSourceMode.Append &&
                (string.IsNullOrWhiteSpace(source.GenerationId) ||
                    source.State is not DurableVectorSourceState.Pending and
                        not DurableVectorSourceState.Committed))
            {
                return false;
            }
        }

        return true;
    }
}
