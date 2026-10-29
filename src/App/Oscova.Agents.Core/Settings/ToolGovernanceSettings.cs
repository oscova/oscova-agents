using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Per-user tool governance metadata used to classify tools and enforce call budgets.
/// </summary>
public sealed class ToolGovernanceSettings : SettingsBase
{
    public const string Key = "tool-governance";

    private readonly Dictionary<string, ToolGovernanceEntry> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ToolGovernanceSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    public IReadOnlyDictionary<string, ToolGovernanceEntry> Tools => _tools;

    /// <summary>Keeps each policy mutation and its persisted snapshot in the same order.</summary>
    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Finds governance metadata for a normalized qualified tool name.
    /// </summary>
    public ToolGovernanceEntry? Find(string qualifiedName)
    {
        var normalizedName = ToolNaming.NormalizeQualifier(qualifiedName);
        return _tools.TryGetValue(normalizedName, out var entry) ? entry : null;
    }

    /// <summary>
    /// Inserts or replaces governance metadata for one qualified tool.
    /// </summary>
    public void Upsert(ToolGovernanceEntry entry)
    {
        var normalizedName = ToolNaming.NormalizeQualifier(entry.QualifiedName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return;
        }

        if (entry.CallLimit is { } limit && !limit.IsValid())
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Tool call limits must use 1-1000000 calls per 24 hours.");
        }

        _tools[normalizedName] = entry with { QualifiedName = normalizedName };
        OnChanged();
        OnPropertyChanged(nameof(Tools));
    }

    /// <summary>
    /// Removes governance metadata for one qualified tool.
    /// </summary>
    public void Remove(string qualifiedName)
    {
        var normalizedName = ToolNaming.NormalizeQualifier(qualifiedName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return;
        }

        if (_tools.Remove(normalizedName))
        {
            OnChanged();
            OnPropertyChanged(nameof(Tools));
        }
    }

    public override string ToYaml() => Yaml.Serialize(new Snapshot
    {
        Tools = [.. _tools.Values.OrderBy(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase)]
    });

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        _tools.Clear();
        if (data?.Tools is null)
        {
            return;
        }

        foreach (var entry in data.Tools)
        {
            var normalizedName = ToolNaming.NormalizeQualifier(entry.QualifiedName);
            if (string.IsNullOrWhiteSpace(normalizedName) || !IsValid(entry))
            {
                continue;
            }

            _tools[normalizedName] = entry with { QualifiedName = normalizedName };
        }
    }

    private static bool IsValid(ToolGovernanceEntry entry)
    {
        var limit = entry.CallLimit;
        return limit?.IsValid() != false;
    }

    private sealed class Snapshot
    {
        public List<ToolGovernanceEntry>? Tools { get; set; }
    }
}
