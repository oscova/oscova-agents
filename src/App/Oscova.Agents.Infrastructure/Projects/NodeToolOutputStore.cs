using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Projects;

/// <summary>Collects node tool output for storage under the user's central vault.</summary>
public sealed class NodeToolOutputStore : IToolOutputStore
{
    private readonly AsyncLocal<List<NodeOutput>?> _current = new();

    /// <summary>Begins an invocation-local output collection.</summary>
    public void Begin() => _current.Value = [];

    /// <summary>Returns collected output and releases the invocation-local reference.</summary>
    public IReadOnlyList<NodeOutput> Finish()
    {
        IReadOnlyList<NodeOutput> result = _current.Value?.ToArray() ?? [];
        _current.Value = null;
        return result;
    }

    /// <inheritdoc />
    public string? Save(string label, string content)
    {
        if (_current.Value is not { } output) { return null; }

        var marker = $"oscova-node-output:{Guid.NewGuid():N}";
        output.Add(new(marker, label, content));
        return marker;
    }
}
