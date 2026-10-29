using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Conversations;
using Oscova.Agents.Persistence;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Persists encrypted goal controls and keeps incognito goals in the retained user scope.</summary>
public sealed class ConversationGoalStore(IAppSettingsRepository settings)
{
    public const string SettingsKey = "conversation-goals";
    private static readonly YamlConverter Yaml = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ChatGoal> _incognito = new(StringComparer.Ordinal);
    private Dictionary<string, ChatGoal>? _saved;
    public event EventHandler? Changed;

    /// <summary>Reads the conversation's current continuation state.</summary>
    public Task<ChatGoal?> GetAsync(string userId, ChatThread thread, CancellationToken ct = default) =>
        UpdateAsync(userId, thread, goal => goal, ct);

    /// <summary>Serializes state transitions so stale runs cannot overwrite an edited or cleared goal.</summary>
    public async Task<ChatGoal?> UpdateAsync(string userId, ChatThread thread,
        Func<ChatGoal?, ChatGoal?> update, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var values = thread.IsIncognito ? _incognito : await ReadAsync(userId, ct).ConfigureAwait(false);
            var previous = values.GetValueOrDefault(thread.Id);
            var goal = update(previous);
            if (goal == previous) { return goal; }

            var proposed = new Dictionary<string, ChatGoal>(values, StringComparer.Ordinal);
            if (goal is null) { proposed.Remove(thread.Id); }
            else { proposed[thread.Id] = goal; }

            if (!thread.IsIncognito)
            {
                await settings.SaveSettingsAsync(userId, SettingsKey, Yaml.Serialize(proposed), ct).ConfigureAwait(false);
                _saved = proposed;
            }
            else if (goal is null) { _incognito.Remove(thread.Id); }
            else { _incognito[thread.Id] = goal; }

            Changed?.Invoke(this, EventArgs.Empty);
            return goal;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes continuation when its conversation is deleted.</summary>
    public async Task DeleteAsync(string userId, ChatThread thread, CancellationToken ct = default) =>
        _ = await UpdateAsync(userId, thread, _ => null, ct).ConfigureAwait(false);

    /// <summary>Removes goals when all of the user's conversations are deleted.</summary>
    public async Task DeleteAllAsync(string userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await settings.SaveSettingsAsync(userId, SettingsKey,
                Yaml.Serialize(new Dictionary<string, ChatGoal>()), ct).ConfigureAwait(false);
            _saved = new(StringComparer.Ordinal);
            _incognito.Clear();
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, ChatGoal>> ReadAsync(string userId, CancellationToken ct)
    {
        if (_saved is not null) { return _saved; }

        var yaml = await settings.GetSettingsAsync(userId, SettingsKey, ct).ConfigureAwait(false);
        var values = yaml is null ? new Dictionary<string, ChatGoal>(StringComparer.Ordinal)
            : Yaml.Deserialize<Dictionary<string, ChatGoal>>(yaml)
                ?? throw new InvalidDataException("Saved conversation goals could not be read.");
        if (values.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value.Objective)
            || !Enum.IsDefined(item.Value.Status) || item.Value.MaxIterations < 1 || item.Value.Iterations < 0))
        {
            throw new InvalidDataException("Saved conversation goals contain invalid state.");
        }
        // A process restart never resumes unattended work or counts its downtime.
        var changed = false;
        foreach (var (id, goal) in values.ToArray())
        {
            if (goal.Status != ChatGoalStatus.Active) { continue; }

            values[id] = goal with { Status = ChatGoalStatus.Paused, Reason = "App restarted", ActiveSince = null, RunId = null };
            changed = true;
        }

        if (changed) { await settings.SaveSettingsAsync(userId, SettingsKey, Yaml.Serialize(values), ct).ConfigureAwait(false); }

        return _saved = values;
    }
}
