using Microsoft.Extensions.AI;
using Oscova.Agents.Definitions;
using Oscova.Agents.Workspace;
using System;
using System.Collections.Generic;

namespace Oscova.Agents.Runtime;

/// <summary>
/// Applies host-owned run scope middleware while the chat client pipeline is being built.
/// </summary>
public interface IAgentRunScopeFactory
{
    /// <summary>
    /// Returns a builder whose runs re-establish ambient state for the supplied agent definition.
    /// </summary>
    ChatClientBuilder Apply(
        ChatClientBuilder builder,
        AgentDefinition definition,
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver);
}
