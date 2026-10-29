using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Definitions;
using Oscova.Agents.Providers;
using Oscova.Agents.Policies;
using Oscova.Agents.Workspace;
using System.Text.Json;
using System.ComponentModel;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Keeps delegated file access and approval decisions inside the child invocation's lifetime.</summary>
public sealed class ApprovingAgentAsToolProvider(
    AgentDefinition parentDefinition,
    IReadOnlyList<AgentDefinition> buildDefinitions,
    AIAgentCollection agents,
    AgentCompositionService crew,
    IAgentDefinitionRepository definitions,
    ProjectAccessService projects,
    PrivacySettings privacy,
    RunContextAccessor accessor,
    ToolApprovalRunLoop approvalLoop,
    Func<CancellationToken, Task<ChatClientAgent>>? createSelfWorker = null) : AgentAsToolProvider(
        parentDefinition.AgentToolIds,
        agents,
        () => accessor.Current?.DelegatedWork,
        parentDefinition.Id,
        createSelfWorker)
{
    // Copy mutable definitions so a pending decision stays bound to the policy used to build its tools.
    private readonly IReadOnlyList<AgentDefinition> _builtDefinitions =
        JsonSerializer.Deserialize<AgentDefinition[]>(JsonSerializer.Serialize(buildDefinitions))!;
    private readonly IReadOnlyDictionary<string, NetworkScope> _builtToolScopes =
        crew.Tools.ToDictionary(tool => tool.Name, tool => crew.Tools.GetNetworkScope(tool.Name), StringComparer.Ordinal);

    protected override AIFunction CreateAgentFunction(ChatClientAgent agent, AgentSession? parentSession)
    {
        var delegated = base.CreateAgentFunction(agent, parentSession);
        async Task<string> InvokeAsync(
            [Description("Question or task for this agent.")] string query,
            [Description("chat: consult this agent here using its retained shared chat history.\nChoose chat when the user asks this agent to join the current discussion.\nchat requires the user to have already invited this agent.\ndelegated: separate work with separate history, the default.\nChoose delegated when the user asks for separate work.")] string destination = "delegated",
            [Description("Optional delegated conversation name.\nReuse the same name to continue that work.\nOmit when destination is chat.")] string? conversation = null,
            [Description("Applies only when destination is chat.\ntrue: continue this agent's shared chat history, the default.\nfalse: answer only the supplied question in a fresh session.\nfalse preserves the existing shared session and still uses the agent's configured memory and skills.")] bool includeChatHistory = true,
            CancellationToken cancellationToken = default)
        {
            if (destination == "delegated")
            {
                var result = await delegated.InvokeAsync(new AIFunctionArguments
                {
                    ["query"] = query,
                    ["conversation"] = conversation
                }, cancellationToken).ConfigureAwait(false);
                return result?.ToString() ?? string.Empty;
            }

            RequireParent();
            if (destination != "chat") { throw new ArgumentException("destination must be delegated or chat."); }

            if (!string.IsNullOrWhiteSpace(conversation)) { throw new ArgumentException("Omit conversation when destination is chat."); }

            var consultation = accessor.Current?.Consultation
                ?? throw new InvalidOperationException("The chat destination requires an active web conversation. Use delegated work.");
            var options = FunctionInvokingChatClient.CurrentContext?.Options?.AdditionalProperties is { } properties
                ? new AgentRunOptions { AdditionalProperties = properties } : null;
            var response = await consultation.InvokeAsync(agent, query, includeChatHistory,
                (recipient, messages, session, ct) => RunAgentAsync(recipient, messages, session, options, null, ct), cancellationToken).ConfigureAwait(false);
            return response.Text;
        }

        return AIFunctionFactory.Create(InvokeAsync, new AIFunctionFactoryOptions
        {
            Name = delegated.Name,
            Description = $"{delegated.Description}\nConsult this agent in the current chat or delegate separate work.",
            AdditionalProperties = delegated.AdditionalProperties
        });
    }

    protected override async Task<AgentResponse> RunAgentAsync(
        ChatClientAgent agent,
        IEnumerable<ChatMessage> messages,
        AgentSession session,
        AgentRunOptions? options,
        Action<AgentResponseUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        var parent = accessor.Current
            ?? throw new InvalidOperationException("Delegation requires an active conversation.");
        if (parent.ToolApproval is not { Channel: ToolApprovalChannel.WebUI } approval)
        {
            throw new InvalidOperationException("Delegated approval is unavailable. Start a new web conversation run.");
        }

        var child = _builtDefinitions.FirstOrDefault(definition => definition.Id == agent.Id)
            ?? throw new InvalidOperationException("The delegated agent is unavailable. Start a fresh run.");
        // A same-agent worker runs under its parent's identity, chain and grants.
        var isSelf = child.Id == parentDefinition.Id;
        IReadOnlyList<string> chain = (parent.DelegationAgentIds.Count, isSelf) switch
        {
            (0, true) => [parent.CurrentAgentId!],
            (_, true) => parent.DelegationAgentIds,
            (0, false) => [parent.CurrentAgentId!, child.Id],
            _ => [.. parent.DelegationAgentIds, child.Id]
        };
        var grants = isSelf
            ? parent.AllowedDirectories
            : crew.ResolveDelegatedDirectories(parent.UserId, child, parent.AllowedDirectories);
        var localOnly = privacy.IsLocalOnly;
        var childContext = parent.With(
            airGapped: parent.AirGapped || localOnly || child.IsLocalOnly,
            currentAgentId: child.Id,
            currentAgentName: child.Name,
            allowedDirectories: grants,
            delegationAgentIds: chain);

        async Task ValidateAsync(CancellationToken ct)
        {
            if (privacy.IsLocalOnly != localOnly) { throw PolicyChanged(); }

            if (_builtToolScopes.Any(tool => crew.Tools.GetNetworkScope(tool.Key) != tool.Value)) { throw PolicyChanged(); }
            // Preserve the root run's grant order while refreshing its project access.
            var standard = parent.StandardRunDirectories.Where(directory => !directory.IsWorkingDirectory)
                .Concat(projects.ResolveRunDirectories(parent.ProjectId, parent.ExecutionProviderId)).ToArray();
            IReadOnlyList<AllowedDirectory>? currentGrants = null;
            AgentDefinition? previous = null;
            foreach (var id in chain)
            {
                var current = await definitions.GetAgentAsync(parent.UserId, id, ct).ConfigureAwait(false);
                var built = _builtDefinitions.FirstOrDefault(definition => definition.Id == id);
                if (current is null || built is null || !SamePolicy(built, current) ||
                    previous is not null && (!previous.AgentToolIds.Contains(id) || !current.IsExposedAsTool))
                {
                    throw PolicyChanged();
                }

                currentGrants = currentGrants is null
                    ? crew.ResolveAllowedDirectories(parent.UserId, current, standard)
                    : crew.ResolveDelegatedDirectories(parent.UserId, current, currentGrants);
                previous = current;
            }

            if (currentGrants?.SequenceEqual(grants) != true) { throw PolicyChanged(); }
        }

        var scope = accessor.SetContext(childContext);
        await using (scope.ConfigureAwait(false))
        {
            return await approvalLoop.RunAsync(agent, messages, session, options, approval,
                cancellationToken, ValidateAsync,
                // A worker reports progress to its own state, never to a consultation card.
                onUpdate ?? (childContext.Consultation?.StreamingAgentId == agent.Id ? childContext.Consultation.OnUpdate : null))
                .ConfigureAwait(false);
        }
    }

    private static InvalidOperationException PolicyChanged() =>
        new("Agent access or delegation policy changed. Start a fresh run before approving another action.");

    private static bool SamePolicy(AgentDefinition first, AgentDefinition second) =>
        first.IsLocalOnly == second.IsLocalOnly &&
        first.IsSubagentDelegationEnabled == second.IsSubagentDelegationEnabled &&
        first.UseCallerModelForConsultations == second.UseCallerModelForConsultations &&
        first.ModelOverrideId == second.ModelOverrideId &&
        first.IsExposedAsTool == second.IsExposedAsTool && first.IsShared == second.IsShared &&
        first.OwnerUserId == second.OwnerUserId && first.IsToolSearchEnabled == second.IsToolSearchEnabled &&
        first.AllowedUserIds.SequenceEqual(second.AllowedUserIds) &&
        first.AllowedToolNames.SequenceEqual(second.AllowedToolNames) &&
        first.PinnedToolNames.SequenceEqual(second.PinnedToolNames) &&
        first.AllowedSkillNames.SequenceEqual(second.AllowedSkillNames) &&
        first.AgentToolIds.SequenceEqual(second.AgentToolIds) &&
        first.AllowedWorkspaces.SequenceEqual(second.AllowedWorkspaces) &&
        first.AllowedDirectories.SequenceEqual(second.AllowedDirectories) &&
        first.IsSharedFolderEnabled == second.IsSharedFolderEnabled &&
        first.ToolPolicies.Select(policy => (policy.ToolName, policy.ApprovalMode))
            .SequenceEqual(second.ToolPolicies.Select(policy => (policy.ToolName, policy.ApprovalMode)));
}
