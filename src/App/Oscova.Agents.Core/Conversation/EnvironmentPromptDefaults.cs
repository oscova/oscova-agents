using Oscova.Agents.Providers;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Provides the editable default environment prompt, adding the product identity to the framework template.
/// </summary>
public static class EnvironmentPromptDefaults
{
    public const string DefaultInstructions =
        "You run inside Oscova Agents. Questions about \"this app\", its features, settings or setup refer to Oscova Agents.\n\n"
        + EnvironmentContextProvider.DefaultEnvironmentPrompt;
}
