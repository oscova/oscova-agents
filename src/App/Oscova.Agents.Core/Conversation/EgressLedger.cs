using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Collects distinct per-agent-run provider egress descriptors for a single user turn.
/// </summary>
public sealed class EgressLedger
{
    private readonly Lock _gate = new();
    private readonly List<ProviderEgressDescriptor> _entries = [];

    /// <summary>
    /// Records a descriptor if this turn has not already seen the same agent/provider/egress tuple.
    /// </summary>
    public void Record(ProviderEgressDescriptor descriptor)
    {
        lock (_gate)
        {
            if (!_entries.Contains(descriptor))
            {
                _entries.Add(descriptor);
            }
        }
    }

    /// <summary>
    /// Returns the descriptors in first-seen order.
    /// </summary>
    public IReadOnlyList<ProviderEgressDescriptor> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }
}
