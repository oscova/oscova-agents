using Oscova.Agents.Speech;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Plugins.OpenAI;

/// <summary>Contributes hosted OpenAI speech providers using host-supplied credentials and transport.</summary>
public sealed class OpenAiAgentPlugin : IAgentPlugin
{
    /// <summary>Registers speech recognition and synthesis without a separate credential declaration.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>().AddSpeechRecognitionProvider(context => new OpenAiSpeechRecognitionProvider(
            context.CreateHttpClient(), context.LoggerFactory.CreateLogger<OpenAiSpeechRecognitionProvider>()))
            .AddSpeechSynthesisProvider(context => new OpenAiSpeechSynthesisProvider(context.CreateHttpClient()))
            .AddSettingsPanel(new("recognition", PluginSettingsSlots.SpeechRecognition, SpeechProviderKeys.OpenAI,
                typeof(SpeechRecognitionSettingsComponent), context => context, PluginSettingsAccess.CurrentUser))
            .AddSettingsPanel(new("synthesis", PluginSettingsSlots.SpeechSynthesis, SpeechProviderKeys.OpenAI,
                typeof(SpeechSynthesisSettingsComponent), context => context, PluginSettingsAccess.CurrentUser));
    }
}
