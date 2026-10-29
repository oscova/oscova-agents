using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Conversations;
using Oscova.Agents.Settings;
using Oscova.Agents.Persistence;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Saves per-conversation choices encrypted while keeping incognito choices in the retained user scope.</summary>
public sealed class ConversationChoiceStore(IAppSettingsRepository settings)
{
    private static readonly YamlConverter Yaml = new();
    public const string SettingsKey = "chat-choices";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ConversationChoice> _incognito = new(StringComparer.Ordinal);

    /// <summary>Reads the saved permission preset independently from transcript history.</summary>
    public async Task<ChatPermissionPreset> GetAsync(string userId, ChatThread thread, CancellationToken ct = default) =>
        (await GetChoiceAsync(userId, thread, ct).ConfigureAwait(false))?.Preset ?? default;

    /// <summary>Reads the model provider last chosen for the conversation, when one was chosen.</summary>
    public async Task<Guid?> GetModelProviderIdAsync(string userId, ChatThread thread, CancellationToken ct = default) =>
        (await GetChoiceAsync(userId, thread, ct).ConfigureAwait(false))?.ModelProviderId;

    /// <summary>Commits the selected preset before acknowledging it to the browser.</summary>
    public Task SaveAsync(string userId, ChatThread thread, ChatPermissionPreset preset, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(preset)) { throw new ArgumentException("Preset must be UseAgentPolicy, AutoApprove, AutoReject or FullAccess."); }

        return UpdateAsync(userId, thread, choice => choice.Preset = preset, ct);
    }

    /// <summary>Remembers the model provider a run was started with so reopening the chat keeps it.</summary>
    public Task SaveModelProviderIdAsync(string userId, ChatThread thread, Guid modelProviderId, CancellationToken ct = default) =>
        UpdateAsync(userId, thread, choice => choice.ModelProviderId = modelProviderId, ct);

    /// <summary>Removes the saved choices when their conversation is deleted.</summary>
    public async Task DeleteAsync(string userId, ChatThread thread, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _incognito.Remove(thread.Id);
            if (thread.IsIncognito) { return; }

            var values = await ReadAsync(userId, ct).ConfigureAwait(false);
            if (values.Remove(thread.Id))
            {
                await settings.SaveSettingsAsync(userId, SettingsKey, Yaml.Serialize(values), ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Clears all choices when the account deletes every conversation.</summary>
    public async Task DeleteAllAsync(string userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await settings.SaveSettingsAsync(userId, SettingsKey, Yaml.Serialize(new Dictionary<string, ConversationChoice>()), ct).ConfigureAwait(false);
            _incognito.Clear();
        }
        finally { _gate.Release(); }
    }

    private async Task<ConversationChoice?> GetChoiceAsync(string userId, ChatThread thread, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var values = thread.IsIncognito ? _incognito : await ReadAsync(userId, ct).ConfigureAwait(false);
            return values.GetValueOrDefault(thread.Id);
        }
        finally { _gate.Release(); }
    }

    private async Task UpdateAsync(string userId, ChatThread thread, Action<ConversationChoice> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var values = thread.IsIncognito ? _incognito : await ReadAsync(userId, ct).ConfigureAwait(false);
            if (!values.TryGetValue(thread.Id, out var choice)) { values[thread.Id] = choice = new ConversationChoice(); }

            update(choice);
            if (!thread.IsIncognito)
            {
                await settings.SaveSettingsAsync(userId, SettingsKey, Yaml.Serialize(values), ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, ConversationChoice>> ReadAsync(string userId, CancellationToken ct)
    {
        var yaml = await settings.GetSettingsAsync(userId, SettingsKey, ct).ConfigureAwait(false);
        if (yaml is null) { return new(StringComparer.Ordinal); }

        try
        {
            var values = Yaml.Deserialize<Dictionary<string, ConversationChoice>>(yaml)
                ?? throw new InvalidDataException("The conversation choice map is empty.");
            if (values.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null || !Enum.IsDefined(item.Value.Preset)))
            {
                throw new InvalidDataException("The conversation choice map contains an invalid preset.");
            }

            return new(values, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("Saved chat choices could not be read. Repair or reset chat-choices in your encrypted settings before running this conversation.", ex);
        }
    }

    /// <summary>One conversation's saved permission preset and model choice.</summary>
    private sealed class ConversationChoice
    {
        public ChatPermissionPreset Preset { get; set; }
        public Guid? ModelProviderId { get; set; }
    }
}
