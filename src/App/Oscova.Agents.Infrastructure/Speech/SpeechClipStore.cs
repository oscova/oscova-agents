using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Infrastructure.Speech;

/// <summary>Stores synthesized WAV clips in the Host's existing served TTS cache.</summary>
public sealed class SpeechClipStore
{
    private const string CacheFolderName = "tts";
    private readonly ILogger<SpeechClipStore> _logger;
    private readonly string _rootPath;

    /// <summary>Creates a clip store under the supplied web root.</summary>
    /// <param name="webRootPath">The fully qualified Host web-root path.</param>
    /// <param name="logger">The logger used for non-fatal storage failures.</param>
    public SpeechClipStore(string webRootPath, ILogger<SpeechClipStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webRootPath);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _rootPath = Path.Combine(webRootPath, CacheFolderName);
    }

    /// <summary>Writes complete WAV bytes and returns the Host-served relative URL, or null when storage fails.</summary>
    public async Task<string?> SaveAsync(
        string ownerId,
        byte[] wavBytes,
        CancellationToken ct = default)
    {
        var ownerFolderName = GetOwnerFolderName(ownerId);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}.wav";

        try
        {
            var directory = Path.Combine(_rootPath, ownerFolderName);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), wavBytes, ct).ConfigureAwait(false);
            return $"/tts/{Uri.EscapeDataString(ownerFolderName)}/{Uri.EscapeDataString(fileName)}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save speech synthesis WAV for owner '{OwnerId}'.", ownerId);
            return null;
        }
    }

    /// <summary>Clears all generated WAV clips.</summary>
    public Task ClearAllAsync(CancellationToken ct = default) => ClearDirectoryAsync(_rootPath, ct);

    /// <summary>Clears generated WAV clips owned by one scoped user or session identifier.</summary>
    public Task ClearAsync(string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return Task.CompletedTask;
        }

        return ClearDirectoryAsync(Path.Combine(_rootPath, GetOwnerFolderName(ownerId)), ct);
    }

    private Task ClearDirectoryAsync(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.wav", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete speech synthesis WAV '{Path}'.", file);
                }
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                ct.ThrowIfCancellationRequested();
                DeleteIfEmpty(childDirectory);
            }

            DeleteIfEmpty(directory);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear speech synthesis cache '{Path}'.", directory);
        }

        return Task.CompletedTask;
    }

    private void DeleteIfEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove empty speech synthesis cache directory '{Path}'.", directory);
        }
    }

    private static string GetOwnerFolderName(string value)
    {
        // Hash the full key so lossy filesystem normalization cannot merge separate sessions.
        var ownerKey = string.IsNullOrWhiteSpace(value) ? "user" : value;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey)));
    }
}
