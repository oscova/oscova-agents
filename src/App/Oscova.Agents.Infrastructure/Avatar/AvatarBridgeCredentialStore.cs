using Oscova.Agents.Core;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Persistence;
using Oscova.Agents.Settings;
using Oscova.Agents.Utils;
using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Infrastructure.Avatar;

/// <summary>Stores one installation-scoped Avatar Bridge bearer credential.</summary>
public sealed class AvatarBridgeCredentialStore(
    IAppSettingsRepository repository,
    string installationKeyPath)
{
    private const string SettingsKey = "avatar-bridge-credentials";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly YamlConverter Yaml = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> LoadTokenAsync(string normalizedUrl, Guid engineId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AvatarFileLease.AcquireAsync(installationKeyPath + ".lock", CancellationToken.None).ConfigureAwait(false);
            string? content;
            try
            {
                content = await repository
                    .GetSettingsAsync(WellKnownUsers.SystemId, SettingsKey)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(content) || !TryReadKey(out var key))
            {
                return null;
            }

            Snapshot? snapshot;
            try
            {
                snapshot = Yaml.Deserialize<Snapshot>(content);
            }
            catch
            {
                return null;
            }

            if (snapshot is null ||
                snapshot.Version != 1 ||
                snapshot.EngineId != engineId ||
                !string.Equals(snapshot.BridgeUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase) ||
                snapshot.ClientId == Guid.Empty)
            {
                return null;
            }

            return TryUnprotect(snapshot, key, out var token) ? token : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string normalizedUrl,
        Guid engineId,
        Guid clientId,
        string bridgeToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeToken);
        if (engineId == Guid.Empty || clientId == Guid.Empty)
        {
            throw new ArgumentException("Engine and client IDs are required.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var lease = await AvatarFileLease.AcquireAsync(installationKeyPath + ".lock", CancellationToken.None).ConfigureAwait(false);
            if (!TryReadKey(out var key))
            {
                key = RandomNumberGenerator.GetBytes(KeySize);
                // A consumed pairing code is the only safe opportunity to repair a missing key.
                await FileUtil.WriteAllTextAtomicAsync(
                    installationKeyPath,
                    Convert.ToBase64String(key)).ConfigureAwait(false);
            }

            var snapshot = Protect(normalizedUrl, engineId, clientId, bridgeToken, key);
            // Serialize the complete envelope before replacing the previous usable credential.
            var content = Yaml.Serialize(snapshot);
            await repository
                .SaveSettingsAsync(WellKnownUsers.SystemId, SettingsKey, content)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryReadKey(out byte[] key)
    {
        key = [];
        try
        {
            if (!File.Exists(installationKeyPath))
            {
                return false;
            }

            key = Convert.FromBase64String(File.ReadAllText(installationKeyPath).Trim());
            return key.Length == KeySize;
        }
        catch
        {
            key = [];
            return false;
        }
    }

    private static Snapshot Protect(
        string normalizedUrl,
        Guid engineId,
        Guid clientId,
        string bridgeToken,
        byte[] key)
    {
        var plaintext = Encoding.UTF8.GetBytes(bridgeToken);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAssociatedData(normalizedUrl, engineId, clientId));

        return new Snapshot
        {
            BridgeUrl = normalizedUrl,
            EngineId = engineId,
            ClientId = clientId,
            ProtectedBridgeToken = string.Join(
                '.',
                "v1",
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(ciphertext))
        };
    }

    private static bool TryUnprotect(Snapshot snapshot, byte[] key, out string token)
    {
        token = string.Empty;
        try
        {
            var parts = snapshot.ProtectedBridgeToken.Split('.');
            if (parts is not ["v1", _, _, _])
            {
                return false;
            }

            var nonce = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var ciphertext = Convert.FromBase64String(parts[3]);
            if (nonce.Length != NonceSize || tag.Length != TagSize || ciphertext.Length == 0)
            {
                return false;
            }

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                BuildAssociatedData(snapshot.BridgeUrl, snapshot.EngineId, snapshot.ClientId));
            token = Encoding.UTF8.GetString(plaintext);
            return !string.IsNullOrWhiteSpace(token);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] BuildAssociatedData(string normalizedUrl, Guid engineId, Guid clientId) =>
        Encoding.UTF8.GetBytes($"{normalizedUrl}\n{engineId:D}\n{clientId:D}");

    private sealed class Snapshot
    {
        public int Version { get; init; } = 1;
        public string BridgeUrl { get; init; } = string.Empty;
        public Guid EngineId { get; init; }
        public Guid ClientId { get; init; }
        public string ProtectedBridgeToken { get; init; } = string.Empty;
    }
}
