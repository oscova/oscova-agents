using Oscova.Agents.Credentials;
using Oscova.Agents.Plugins.ElevenLabs.Settings;

namespace Oscova.Agents.Plugins.ElevenLabs;

/// <summary>Contributes optional speech providers and their user-owned credential and settings panels.</summary>
public sealed class ElevenLabsAgentPlugin : IAgentPlugin
{
    /// <summary>Identifies both speech capabilities contributed by this plugin.</summary>
    public const string ProviderType = "ElevenLabs";

    /// <summary>Registers speech capabilities without resolving credentials or contacting the service.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>()
            .AddCredential(new CredentialDeclaration
            {
                Key = "elevenlabs", Label = "ElevenLabs", LabelKey = CredentialTextKeys.Label,
                Description = "API key for ElevenLabs speech recognition and synthesis", DescriptionKey = CredentialTextKeys.Description,
                Scope = CredentialScope.User,
                Fields = [new CredentialFieldDefinition
                {
                    Name = "apiKey", Label = "API Key", LabelKey = CredentialTextKeys.ApiKeyLabel,
                    IsSecret = true, IsRequired = true, DefaultEnvVariable = "ELEVENLABS_API_KEY"
                }]
            })
            .AddSpeechRecognitionProvider(context => new ElevenLabsSpeechRecognitionProvider(context.CreateHttpClient))
            .AddSpeechSynthesisProvider(context => new ElevenLabsSpeechSynthesisProvider(context.CreateHttpClient))
            .AddSettingsPanel(new("recognition", PluginSettingsSlots.SpeechRecognition, ProviderType,
                typeof(SpeechRecognitionSettingsComponent), context => context, PluginSettingsAccess.CurrentUser))
            .AddSettingsPanel(new("synthesis", PluginSettingsSlots.SpeechSynthesis, ProviderType,
                typeof(SpeechSynthesisSettingsComponent), context => context, PluginSettingsAccess.CurrentUser));
    }
}
