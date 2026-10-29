using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Notifications;
using Oscova.Agents.Settings;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Notifications;

/// <summary>
/// Maintains the small JSONL-backed notification inbox used by the web shell bell.
/// </summary>
public sealed class NotificationInbox : INotificationInbox, IDisposable
{
    private const int MaxEntriesPerScope = 50;
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAppSettingsRepository _settingsRepository;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NotificationInbox> _logger;
    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<string, List<NotificationEntry>> _mirror = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _writeGate = new();

    public NotificationInbox(
        NotificationHub<NotificationMessage> hub,
        IAppSettingsRepository settingsRepository,
        ILoggerFactory loggerFactory)
    {
        _settingsRepository = settingsRepository;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NotificationInbox>();
        _subscription = hub.SubscribeAll(OnNotificationAsync);
    }

    public Task<IReadOnlyList<NotificationEntry>> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return Task.FromResult<IReadOnlyList<NotificationEntry>>(Load(UserPath(userId), isSystem: false));
    }

    public Task<IReadOnlyList<NotificationEntry>> GetSystemAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<NotificationEntry>>(Load(SystemPath(), isSystem: true));
    }

    public async Task<IReadOnlyList<NotificationEntry>> GetSystemForUserAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var entries = Load(SystemPath(), isSystem: true);
        return await FilterVisibleForUserAsync(userId, entries).ConfigureAwait(false);
    }

    public async Task<bool> ShouldDeliverToUserAsync(string userId, NotificationKind kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return !await ShouldDropNotificationAsync(userId, kind, forLiveDelivery: true).ConfigureAwait(false);
    }

    public Task MarkAllReadAsync(string userId, bool includeSystem, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Mutate(UserPath(userId), isSystem: false, entries => entries.Select(entry => entry with { IsRead = true }).ToList());
        if (includeSystem)
        {
            Mutate(SystemPath(), isSystem: true, entries => entries.Select(entry => entry with { IsRead = true }).ToList());
        }

        return Task.CompletedTask;
    }

    public Task MarkReadAsync(string userId, string entryId, bool isSystemEntry, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        Mutate(isSystemEntry ? SystemPath() : UserPath(userId), isSystemEntry, entries => entries
            .Select(entry => string.Equals(entry.Id, entryId, StringComparison.OrdinalIgnoreCase)
                ? entry with { IsRead = true }
                : entry)
            .ToList());

        return Task.CompletedTask;
    }

    public Task ClearAsync(string userId, bool includeSystem, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Mutate(UserPath(userId), isSystem: false, _ => []);
        if (includeSystem)
        {
            Mutate(SystemPath(), isSystem: true, _ => []);
        }

        return Task.CompletedTask;
    }

    public Task RemoveSystemAsync(NotificationKind kind, CancellationToken ct = default)
    {
        RewriteLines(SystemPath(), entry => entry.Kind == kind ? null : entry);
        return Task.CompletedTask;
    }

    public Task RewriteForUserAsync(string userId, Func<NotificationEntry, NotificationEntry> rewrite, CancellationToken ct = default)
    {
        RewriteLines(UserPath(userId), rewrite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }

    private async Task OnNotificationAsync(string? userId, NotificationMessage message)
    {
        var targetUserId = string.IsNullOrWhiteSpace(userId) ? message.UserId : userId;
        var isSystem = string.IsNullOrWhiteSpace(targetUserId);

        if (!isSystem && await ShouldDropNotificationAsync(targetUserId!, message.Kind).ConfigureAwait(false))
        {
            return;
        }

        var entry = new NotificationEntry(
            Guid.NewGuid().ToString("N"),
            message.Title,
            message.Body,
            message.Kind,
            DateTimeOffset.UtcNow,
            IsRead: false,
            message.AgentId,
            message.TaskId,
            message.RunId,
            message.ThreadId,
            IsSystem: isSystem,
            PluginId: message.PluginId);

        Append(isSystem ? SystemPath() : UserPath(targetUserId!), entry);
    }

    private async Task<List<NotificationEntry>> FilterVisibleForUserAsync(string userId, IReadOnlyList<NotificationEntry> entries)
    {
        try
        {
            var settings = new AssistantUiSettings(_settingsRepository, userId, _loggerFactory);
            await settings.LoadAsync().ConfigureAwait(false);
            return entries
                .Where(entry => !ShouldDropNotification(settings, entry.Kind))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read notification preferences for user '{UserId}'.", userId);
            return entries.ToList();
        }
    }

    private async Task<bool> ShouldDropNotificationAsync(string userId, NotificationKind kind, bool forLiveDelivery = false)
    {
        try
        {
            var settings = new AssistantUiSettings(_settingsRepository, userId, _loggerFactory);
            await settings.LoadAsync().ConfigureAwait(false);
            // Mute applies to live alerts, while the inbox still stores allowed notices.
            return (forLiveDelivery && settings.AreNotificationsMuted) || ShouldDropNotification(settings, kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read notification preferences for user '{UserId}'.", userId);
            return false;
        }
    }

    private static bool ShouldDropNotification(AssistantUiSettings settings, NotificationKind kind)
    {
        if (kind is NotificationKind.SystemWarning && !settings.ShowLogWarningNotifications)
        {
            return true;
        }

        if (kind is NotificationKind.SystemError && !settings.ShowSystemErrorNotifications)
        {
            return true;
        }

        return false;
    }

    private void Append(string path, NotificationEntry entry)
    {
        Mutate(path, entry.IsSystem, entries =>
        {
            var mutable = entries.ToList();
            var last = mutable.Count == 0 ? null : mutable[^1];
            if (last is not null && IsDuplicate(last, entry))
            {
                mutable[^1] = entry with { Id = last.Id };
            }
            else
            {
                mutable.Add(entry);
            }

            return mutable
                .OrderByDescending(item => item.CreatedAt)
                .Take(MaxEntriesPerScope)
                .OrderBy(item => item.CreatedAt)
                .ToList();
        });
    }

    private static bool IsDuplicate(NotificationEntry last, NotificationEntry next)
    {
        // Release discovery owns durable suppression, so distinct release notices must remain distinct here.
        if (next.Kind is NotificationKind.HarnessUpdate or NotificationKind.AvatarUpdate) { return false; }
        // Distinct task runs keep distinct inbox entries even when their stock titles match.
        return last.Kind == next.Kind
            && string.Equals(last.TaskId, next.TaskId, StringComparison.Ordinal)
            && string.Equals(last.RunId, next.RunId, StringComparison.Ordinal)
            && string.Equals(last.Title, next.Title, StringComparison.Ordinal)
            && string.Equals(last.ThreadId, next.ThreadId, StringComparison.Ordinal)
            && next.CreatedAt - last.CreatedAt <= DedupeWindow;
    }

    private void Mutate(string path, bool isSystem, Func<IReadOnlyList<NotificationEntry>, List<NotificationEntry>> update)
    {
        lock (_writeGate)
        {
            var current = Load(path, isSystem);
            var next = update(current);
            Write(path, next);
            _mirror[path] = next;
        }
    }

    // Copies malformed lines verbatim, unlike Mutate, so a one-time rewrite never loses a stored line.
    private void RewriteLines(string path, Func<NotificationEntry, NotificationEntry?> rewrite)
    {
        lock (_writeGate)
        {
            if (!File.Exists(path)) { return; }

            var lines = new List<string>();
            foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                NotificationEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<NotificationEntry>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    entry = null;
                }

                if (entry is null)
                {
                    lines.Add(line);
                }
                else if (rewrite(entry) is { } next)
                {
                    lines.Add(JsonSerializer.Serialize(next, JsonOptions));
                }
            }

            WriteLines(path, lines);
            _mirror.TryRemove(path, out _);
        }
    }

    private List<NotificationEntry> Load(string path, bool isSystem)
    {
        if (_mirror.TryGetValue(path, out var cached))
        {
            return cached;
        }

        lock (_writeGate)
        {
            if (_mirror.TryGetValue(path, out cached))
            {
                return cached;
            }

            var entries = Read(path, isSystem);
            _mirror[path] = entries;
            return entries;
        }
    }

    private List<NotificationEntry> Read(string path, bool isSystem)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<NotificationEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<NotificationEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry with { IsSystem = isSystem });
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipped malformed notification inbox line from '{Path}'.", path);
            }
        }

        return entries
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(MaxEntriesPerScope)
            .OrderBy(entry => entry.CreatedAt)
            .ToList();
    }

    private static void Write(string path, IReadOnlyList<NotificationEntry> entries) =>
        WriteLines(path, entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));

    // An interrupted write leaves the previous inbox intact.
    private static void WriteLines(string path, IEnumerable<string> lines) =>
        FileUtil.WriteAllTextAtomic(path, string.Concat(lines.Select(line => line + Environment.NewLine)));

    private static string UserPath(string userId)
    {
        return Path.Combine(PathUtil.AppDataPath, "users", userId, "notifications", "inbox.jsonl");
    }

    private static string SystemPath()
    {
        return Path.Combine(PathUtil.AppDataPath, "notifications", "system-inbox.jsonl");
    }
}
