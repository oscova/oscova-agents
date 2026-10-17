namespace Oscova.Agents.Tests.Infrastructure.TestDoubles;

/// <summary>Stores agent definitions in memory for tests.</summary>
public sealed class InMemoryAgentDefinitionRepository : IAgentDefinitionRepository
{
    private readonly Dictionary<string, AgentDefinition> _agents = [];

    public Task<AgentDefinition?> GetAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        _agents.TryGetValue(agentId, out var agent);
        return Task.FromResult(agent);
    }

    public Task<AgentDefinition?> GetMainAgentAsync(string userId, CancellationToken ct = default)
    {
        var main = _agents.Values.FirstOrDefault(agent => agent.IsMain);
        return Task.FromResult(main);
    }

    public Task<IReadOnlyList<AgentDefinition>> GetAllAgentsAsync(string userId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents.Values]);
    }
    public Task SaveInstructionSourceAsync(string userId, AgentDefinition agent, CancellationToken ct = default) => SaveAgentAsync(userId, agent, ct);

    public Task SaveAgentAsync(string userId, AgentDefinition agent, CancellationToken ct = default, string? creationDirectoryName = null)
    {
        _agents[agent.Id] = agent;
        return Task.CompletedTask;
    }

    public Task EnsureMainAgentAsync(string userId, CancellationToken ct = default)
    {
        if (_agents.Values.Any(agent => agent.IsMain))
        {
            return Task.CompletedTask;
        }

        var agent = new AgentDefinition { Id = Guid.NewGuid().ToString(), IsMain = true };
        _agents[agent.Id] = agent;
        return Task.CompletedTask;
    }

    public Task DeleteAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        _agents.Remove(agentId);
        return Task.CompletedTask;
    }
}