using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Definitions;
using Oscova.Agents.Workspace;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Runtime;

/// <summary>
/// Carries all host-prepared inputs required to build or retrieve an agent.
/// </summary>
/// <param name="ChatClientFactory">Creates the model-backed chat client for a specific agent definition.</param>
/// <param name="AgentDefinition">The definition for the agent requested by the host.</param>
/// <param name="AvailableAgentDefinitions">All definitions available for resolving agent-as-tool dependencies.</param>
/// <param name="ContextProviderFactory">The factory that creates context providers for a specific definition.</param>
/// <param name="WorkspaceResolver">The resolver that supplies the workspaces allowed for a specific definition.</param>
/// <param name="ConfigureBuilder">An optional callback that can add host middleware to the main requested agent.</param>
/// <param name="ConfigureOptions">An optional callback that can adjust framework agent options for the main requested agent.</param>
/// <param name="BypassRequestedAgentCache">Whether the requested agent must be built fresh for this run.</param>
/// <param name="ChatReducerFactory">Creates a chat reducer for an agent definition when compaction is enabled.</param>
/// <param name="ConfigureAgentBuilder">Adds host middleware to every built agent, including exposed agent-tool dependencies.</param>
/// <param name="ConfigureHarnessBuilder">Adds always-on harness middleware to every built agent. Unlike the
/// customizer delegates, this does not affect agent caching.</param>
/// <param name="ChatOptionsResolver">Resolves the default model request options for each built agent.</param>
public sealed record AgentBuildRequest(
    Func<AgentDefinition, CancellationToken, Task<IChatClient>> ChatClientFactory,
    AgentDefinition AgentDefinition,
    IReadOnlyList<AgentDefinition> AvailableAgentDefinitions,
    Func<AgentDefinition, CancellationToken, Task<IReadOnlyList<AIContextProvider>>> ContextProviderFactory,
    Func<AgentDefinition, IReadOnlyList<AllowedDirectory>> WorkspaceResolver,
    Func<ChatClientBuilder, ChatClientBuilder>? ConfigureBuilder = null,
    Action<ChatClientAgentOptions>? ConfigureOptions = null,
    bool BypassRequestedAgentCache = false,
    Func<AgentDefinition, IChatClient, CancellationToken, Task<IChatReducer?>>? ChatReducerFactory = null,
    Func<AgentDefinition, ChatClientBuilder, ChatClientBuilder>? ConfigureAgentBuilder = null,
    Func<ChatClientBuilder, ChatClientBuilder>? ConfigureHarnessBuilder = null,
    Func<AgentDefinition, ChatOptions?>? ChatOptionsResolver = null);
