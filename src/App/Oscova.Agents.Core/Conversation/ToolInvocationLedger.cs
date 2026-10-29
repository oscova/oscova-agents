using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Accumulates tool invocation outcomes for a single run.
/// </summary>
public sealed class ToolInvocationLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string ToolName, string? McpServerId, string? AgentId), ToolAccumulator> _entries = [];

    /// <summary>
    /// Records a successful tool invocation: the plain name, or an empty name with the agent id for an agent called as a tool.
    /// </summary>
    public void RecordCompleted(string toolName, NetworkScope networkScope = NetworkScope.None, string? mcpServerId = null, string? agentId = null) =>
        Record(toolName, networkScope, mcpServerId, agentId, succeeded: true);

    /// <summary>
    /// Records a failed tool invocation.
    /// </summary>
    public void RecordFailed(string toolName, NetworkScope networkScope = NetworkScope.None, bool dispatched = true, string? mcpServerId = null, string? agentId = null) =>
        Record(toolName, networkScope, mcpServerId, agentId, succeeded: false, dispatched);

    /// <summary>
    /// Returns per-tool success and failure counts.
    /// </summary>
    public IReadOnlyList<ToolInvocationRecord> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<ToolInvocationRecord>(_entries.Count);
            foreach (var (key, entry) in _entries)
            {
                result.Add(new ToolInvocationRecord(key.ToolName, entry.Succeeded, entry.Failed, entry.NetworkScope, entry.NotDispatched, key.McpServerId, key.AgentId));
            }

            return result;
        }
    }

    private void Record(string toolName, NetworkScope networkScope, string? mcpServerId, string? agentId, bool succeeded, bool dispatched = true)
    {
        if (string.IsNullOrWhiteSpace(toolName) && string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        lock (_gate)
        {
            var key = (toolName, mcpServerId, agentId);
            if (!_entries.TryGetValue(key, out var accumulator))
            {
                accumulator = new ToolAccumulator();
                _entries[key] = accumulator;
            }

            accumulator.NetworkScope = networkScope;
            if (!dispatched) { accumulator.NotDispatched++; }

            if (succeeded)
            {
                accumulator.Succeeded++;
            }
            else
            {
                accumulator.Failed++;
            }
        }
    }

    private sealed class ToolAccumulator
    {
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int NotDispatched { get; set; }
        public NetworkScope NetworkScope { get; set; }
    }
}
