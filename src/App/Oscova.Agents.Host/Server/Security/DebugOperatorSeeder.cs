#if DEBUG
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Gives the development operator a selected chat model and completed onboarding so test hosts skip the wizard.</summary>
internal static class DebugOperatorSeeder
{
    private const string GeminiKeyVariable = "GEMINI_API_KEY";

    /// <summary>Applies <c>DebugAutoLogin:Seed:Provider</c> (gemini or test) once, while onboarding is not completed.</summary>
    public static async Task SeedAsync(HttpContext context, UserServiceScope scope)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var provider = configuration["DebugAutoLogin:Seed:Provider"];
        if (string.IsNullOrWhiteSpace(provider) || scope.OnboardingSettings.IsCompleted) { return; }

        // The same writes the onboarding wizard's model step and Finish button make.
        var config = provider.Trim().ToLowerInvariant() switch
        {
            "gemini" => ModelProviderConfigPresets.GetPresets().First(preset => preset.Type == ModelProviderKeys.Google),
            DebugTestModelProvider.ProviderType => new ModelProviderConfig { Name = "Test model", Type = DebugTestModelProvider.ProviderType, ModelId = DebugTestModelProvider.ModelId },
            _ => throw new InvalidOperationException($"DebugAutoLogin:Seed:Provider '{provider}' is not supported. Use gemini or test.")
        };
        var model = configuration["DebugAutoLogin:Seed:Model"];
        if (!string.IsNullOrWhiteSpace(model)) { config.ModelId = model.Trim(); }

        if (config.Type == ModelProviderKeys.Google)
        {
            // The credential names the variable, so the key value is never stored.
            var declaration = context.RequestServices.GetRequiredService<ICredentialTypeRegistry>().Find("google")
                ?? throw new InvalidOperationException("Credential slot 'google' is not declared.");
            var field = declaration.Fields.Single();
            config.CredentialKey = declaration.Key;
            await scope.CredentialService.SaveAsync(new CredentialWrite
            {
                CredentialKey = declaration.Key,
                Alias = declaration.Label,
                Description = declaration.Description,
                Fields = [new CredentialFieldWrite { Name = field.Name, IsSecret = field.IsSecret, Source = CredentialFieldSource.Env, EnvVariable = GeminiKeyVariable }]
            }).ConfigureAwait(false);
        }

        scope.ModelProviderSettings.ModelProviderConfigs.Add(config);
        scope.ModelProviderSettings.SelectedChatModelProviderId = config.Id;
        await scope.ModelProviderSettings.SaveAsync().ConfigureAwait(false);
        scope.AppService.NotifyModelProviderSettingsChanged();

        scope.OnboardingSettings.IsCompleted = true;
        scope.OnboardingSettings.CompletedAt = DateTimeOffset.UtcNow;
        await scope.OnboardingSettings.SaveAsync().ConfigureAwait(false);
    }
}
#endif
