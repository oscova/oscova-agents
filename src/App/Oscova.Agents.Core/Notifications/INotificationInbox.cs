namespace Oscova.Agents.Core.Notifications;

/// <summary>
/// Persists and retrieves in-app notifications for user and system scopes.
/// </summary>
public interface INotificationInbox
{
    /// <summary>Returns the current user's notification entries.</summary>
    Task<IReadOnlyList<NotificationEntry>> GetForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Returns system notification entries visible to administrators.</summary>
    Task<IReadOnlyList<NotificationEntry>> GetSystemAsync(CancellationToken ct = default);

    /// <summary>Returns system notification entries visible to one administrator after their UI preferences are applied.</summary>
    Task<IReadOnlyList<NotificationEntry>> GetSystemForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Returns whether a user's UI preferences allow this notification kind to be delivered.</summary>
    Task<bool> ShouldDeliverToUserAsync(string userId, NotificationKind kind, CancellationToken ct = default);

    /// <summary>Marks every visible notification as read.</summary>
    Task MarkAllReadAsync(string userId, bool includeSystem, CancellationToken ct = default);

    /// <summary>Marks one notification as read.</summary>
    Task MarkReadAsync(string userId, string entryId, bool isSystemEntry, CancellationToken ct = default);

    /// <summary>Clears visible notifications.</summary>
    Task ClearAsync(string userId, bool includeSystem, CancellationToken ct = default);

    /// <summary>Removes system notifications of one kind once they no longer apply.</summary>
    Task RemoveSystemAsync(NotificationKind kind, CancellationToken ct = default);

    /// <summary>Rewrites every entry of the user's inbox under the inbox write gate.</summary>
    Task RewriteForUserAsync(string userId, Func<NotificationEntry, NotificationEntry> rewrite, CancellationToken ct = default);
}
