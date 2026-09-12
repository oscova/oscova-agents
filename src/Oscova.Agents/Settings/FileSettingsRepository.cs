using Oscova.Agents.Utils;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Settings;

/// <summary>Stores each settings domain as a file beneath a caller-selected directory.</summary>
public sealed class FileSettingsRepository : IAppSettingsRepository
{
    private readonly Func<string, string> _settingsDirectoryResolver;

    /// <summary>Initializes a file repository with a caller-owned directory resolver.</summary>
    public FileSettingsRepository(Func<string, string> settingsDirectoryResolver)
    {
        _settingsDirectoryResolver = settingsDirectoryResolver ?? throw new ArgumentNullException(nameof(settingsDirectoryResolver));
    }

    /// <inheritdoc />
    public async Task<string?> GetSettingsAsync(string scopeId, string settingsKey, CancellationToken ct = default)
    {
        var path = ResolvePath(scopeId, settingsKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(string scopeId, string settingsKey, string content, CancellationToken ct = default)
    {
        var path = ResolvePath(scopeId, settingsKey);
        await FileUtil.WriteAllTextAtomicAsync(path, content, ct).ConfigureAwait(false);
    }

    private string ResolvePath(string scopeId, string settingsKey)
    {
        if (!PathHelper.IsSafePathSegment(settingsKey))
        {
            throw new ArgumentException($"Invalid settings key '{settingsKey}'.", nameof(settingsKey));
        }

        if (!PathHelper.TryResolveContainedPath(
                _settingsDirectoryResolver(scopeId),
                settingsKey + ".yaml",
                out var path))
        {
            throw new UnauthorizedAccessException($"Settings path escapes its configured directory: {settingsKey}");
        }

        return path;
    }
}
