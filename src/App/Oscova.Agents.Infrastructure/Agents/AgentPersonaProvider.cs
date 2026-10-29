using Oscova.Agents.Core.Personas;
using Oscova.Agents.Infrastructure.Tasks;

namespace Oscova.Agents.Infrastructure.Agents;

/// <summary>
/// Resolves built-in persona system instructions from invariant seed markdown.
/// </summary>
public interface IAgentPersonaProvider
{
    /// <summary>
    /// Returns the templated system instruction for the given persona, or an empty string when no seeded persona is selected.
    /// </summary>
    string GetSystemInstruction(string personaId);
}

/// <summary>
/// Reads persona markdown embedded under the default SeedContent/personas instructions.
/// </summary>
public sealed class AgentPersonaProvider : IAgentPersonaProvider
{
    private const string PersonaFolder = "personas";

    /// <inheritdoc />
    public string GetSystemInstruction(string personaId)
    {
        if (!AgentPersonas.IsSeeded(personaId))
        {
            return string.Empty;
        }

        return SeedContentProvider.GetInstruction($"{PersonaFolder}/{personaId}.md");
    }
}
