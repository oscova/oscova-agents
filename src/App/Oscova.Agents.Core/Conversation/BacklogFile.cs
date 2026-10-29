using System.Collections;
using System.Globalization;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Memory;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Binds the shared knowledge store to the user's to-do backlog, one encrypted document per item.</summary>
public static class BacklogFile
{
    public const string ItemType = "backlog-item";
    public const string OpenState = "open";
    public const string DoneState = "done";
    public const string WithdrawnState = "withdrawn";
    public const string UserAssignee = "user";
    public const string AgentAssignee = "agent";

    /// <summary>Creates the backlog store under the user's directory.</summary>
    public static EncryptedKnowledgeStore CreateStore(string userId, IUserDirectoryService directories,
        IContentProtector protector, IUserFileBackupService fileBackups) =>
        new(userId, directories.GetUserDirectoryPath(userId), UserFileNames.BacklogDirectory,
            UserFileLimits.MaxEditSizeBytes, fileBackups, protector);

    /// <summary>Reads every saved item with its metadata, since the derived index carries no dates or states.</summary>
    public static async Task<IReadOnlyList<BacklogItem>> ReadItemsAsync(EncryptedKnowledgeStore store, CancellationToken ct = default)
    {
        var items = new List<BacklogItem>();
        foreach (var id in store.ListConceptIds(string.Empty))
        {
            if (await store.ReadConceptAsync(id, ct).ConfigureAwait(false) is { } concept)
            {
                items.Add(new BacklogItem(id, concept.Metadata, concept.Body));
            }
        }

        return items;
    }
}

/// <summary>One backlog item document with typed access to the frontmatter fields the backlog writes.</summary>
public sealed record BacklogItem(string Id, IReadOnlyDictionary<string, object?> Metadata, string Body)
{
    public string Title => Text("title") is { Length: > 0 } title ? title : Id;
    public string Description => Text("description");
    public string Assignee => Text("assignee");
    public string State => Text("state");
    public string Evidence => Text("evidence");
    public DateTimeOffset? Added => Time(Metadata.GetValueOrDefault("added"));
    public DateTimeOffset? Completed => Time(Metadata.GetValueOrDefault("completed"));

    /// <summary>The store's last write time, stamped on every save, update and completion.</summary>
    public DateTimeOffset? Changed => Metadata.GetValueOrDefault("generated") is IDictionary generated ? Time(generated["at"]) : null;

    /// <summary>Formats a time for agent-facing text.</summary>
    public static string Format(DateTimeOffset? time) =>
        time?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "unknown";

    private string Text(string key) => Convert.ToString(Metadata.GetValueOrDefault(key), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    // YAML may read an unquoted ISO timestamp back as a date value instead of a string.
    private static DateTimeOffset? Time(object? value) => value switch
    {
        DateTimeOffset time => time,
        DateTime time => new DateTimeOffset(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime()),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) => time,
        _ => null
    };
}
