using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>Stores each user's goal cycle limits without changing shared agent definitions.</summary>
public sealed class AgentGoalSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
    : SettingsBase(repository, userId, loggerFactory)
{
    public const string Key = "agent-goals";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, int> _limits = new(StringComparer.Ordinal);
    private Dictionary<string, int>? _pending;
    public override string SettingsKey => Key;
    public IReadOnlyDictionary<string, int> Limits => _limits;

    /// <summary>Gets the personal limit, with null selecting the completion-driven default.</summary>
    public int? GetLimit(string agentId) => _limits.TryGetValue(agentId, out var value) ? value : null;

    /// <summary>Saves a whole-number limit or removes the personal override.</summary>
    public async Task SetLimitAsync(string agentId, int? limit)
    {
        if (string.IsNullOrWhiteSpace(agentId)) { throw new ArgumentException("AgentId must not be blank."); }

        if (limit is < 1) { throw new ArgumentOutOfRangeException(nameof(limit), "Goal max iterations must be 1 to 2147483647, or blank for Default."); }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await LoadAsync().ConfigureAwait(false);
            var proposed = new Dictionary<string, int>(_limits, StringComparer.Ordinal);
            if (limit is { } value) { proposed[agentId] = value; }
            else { proposed.Remove(agentId); }

            _pending = proposed;
            await SaveAsync().ConfigureAwait(false);
            _limits = proposed;
        }
        finally { _pending = null; _gate.Release(); }
    }

    public override string ToYaml() => Yaml.Serialize(new Snapshot { Limits = _pending ?? _limits });
    public override void FromYaml(string yaml)
    {
        var values = Yaml.Deserialize<Snapshot>(yaml).Limits ?? new();
        if (values.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 1))
        {
            throw new InvalidDataException("Agent goal limits must be 1 to 2147483647.");
        }

        if (_pending is null) { _limits = values; }
        else { _pending = values; }
    }

    private sealed class Snapshot { public Dictionary<string, int>? Limits { get; set; } }
}
