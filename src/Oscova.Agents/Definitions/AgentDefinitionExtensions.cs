using Microsoft.Agents.AI;
using System.Collections.Generic;

namespace Oscova.Agents.Definitions;

/// <summary>
/// Converts framework agent definitions into Microsoft Agent Framework options.
/// </summary>
public static class AgentDefinitionExtensions
{
    /// <summary>
    /// Creates agent framework options from an Oscova agent definition.
    /// </summary>
    public static ChatClientAgentOptions ToChatClientAgentOptions(
        this AgentDefinition agentDefinition,
        IEnumerable<AIContextProvider>? aiContextProviders = null)
    {
        return new ChatClientAgentOptions
        {
            Id = agentDefinition.Id,
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            AIContextProviders = aiContextProviders,
            DisableApprovalNotRequiredFunctionBypassing = false,
        };
    }
}
