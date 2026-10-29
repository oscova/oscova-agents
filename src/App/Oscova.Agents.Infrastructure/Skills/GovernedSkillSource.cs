using System.Text.Json;
using Microsoft.Agents.AI;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;

#pragma warning disable MAAI001

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Attaches managed skill usage to the same cancellation scope as provider and MCP usage.</summary>
public sealed class GovernedSkillSource(AgentSkillsSource inner, string userId,
    SkillGovernanceService governance, ExecutionPermissionRegistry executions, RunContextAccessor runs)
    : DelegatingAgentSkillsSource(inner)
{
    public override async Task<IList<AgentSkill>> GetSkillsAsync(AgentSkillsSourceContext context,
        CancellationToken cancellationToken = default)
    {
        var skills = await base.GetSkillsAsync(context, cancellationToken).ConfigureAwait(false);
        var result = new List<AgentSkill>(skills.Count);
        foreach (var skill in skills)
        {
            var dependency = await governance.GetDependencyAsync(userId, skill.Frontmatter.Name, cancellationToken).ConfigureAwait(false);
            result.Add(dependency is { } value ? new ManagedSkill(skill, value.Key, value.Version, this) : skill);
        }

        return result;
    }

    private ExecutionPermissionRegistry.Registration Register(string key, long version, CancellationToken ct)
    {
        var registration = executions.RegisterConfiguration(key, version, userId, ct);
        try
        {
            runs.Current?.ConfigurationScope?.Track(executions, key, version);
            return registration;
        }
        catch { registration.Dispose(); throw; }
    }

    /// <summary>Rejects retained definitions after their publication changes.</summary>
    private sealed class ManagedSkill(AgentSkill inner, string key, long version, GovernedSkillSource owner) : AgentSkill
    {
        public override AgentSkillFrontmatter Frontmatter => inner.Frontmatter;

        public override async ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
        {
            using var registration = owner.Register(key, version, cancellationToken);
            return await inner.GetContentAsync(registration.Token).ConfigureAwait(false);
        }

        public override async ValueTask<AgentSkillResource?> GetResourceAsync(string name, CancellationToken cancellationToken = default)
        {
            using var registration = owner.Register(key, version, cancellationToken);
            var resource = await inner.GetResourceAsync(name, registration.Token).ConfigureAwait(false);
            return resource is null ? null : new ManagedResource(resource, key, version, owner);
        }

        public override async ValueTask<AgentSkillScript?> GetScriptAsync(string name, CancellationToken cancellationToken = default)
        {
            using var registration = owner.Register(key, version, cancellationToken);
            var script = await inner.GetScriptAsync(name, registration.Token).ConfigureAwait(false);
            return script is null ? null : new ManagedScript(script, inner, key, version, owner);
        }
    }

    /// <summary>Keeps delayed resource reads subject to the captured publication version.</summary>
    private sealed class ManagedResource(AgentSkillResource inner, string key, long version, GovernedSkillSource owner)
        : AgentSkillResource(inner.Name, inner.Description)
    {
        public override async Task<object?> ReadAsync(IServiceProvider? serviceProvider = null, CancellationToken cancellationToken = default)
        {
            using var registration = owner.Register(key, version, cancellationToken);
            return await inner.ReadAsync(serviceProvider, registration.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Keeps script execution registered until the real script and its cleanup finish.</summary>
    private sealed class ManagedScript(AgentSkillScript inner, AgentSkill originalSkill,
        string key, long version, GovernedSkillSource owner) : AgentSkillScript(inner.Name, inner.Description)
    {
        public override JsonElement? ParametersSchema => inner.ParametersSchema;

        public override async Task<object?> RunAsync(AgentSkill skill, JsonElement? arguments,
            IServiceProvider? serviceProvider, CancellationToken cancellationToken = default)
        {
            using var registration = owner.Register(key, version, cancellationToken);
            return await inner.RunAsync(originalSkill, arguments, serviceProvider, registration.Token).ConfigureAwait(false);
        }
    }
}
