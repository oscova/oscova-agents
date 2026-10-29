using Oscova.Agents.Core.Settings;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Core.Secrets;

/// <summary>
/// Owns app settings that persist links to user-renamable credentials.
/// </summary>
public static class CredentialReferenceService
{
    /// <summary>Returns user-facing descriptions of user-scoped settings linked to a credential key.</summary>
    public static IReadOnlyList<string> GetUserReferences(
        ModelProviderSettings settings,
        SpeechSettings speechSettings,
        string credentialKey)
    {
        var references = new List<string>();
        foreach (var provider in settings.OwnedProviders
                     .Where(config => string.Equals(config.CredentialKey, credentialKey, StringComparison.OrdinalIgnoreCase)))
        {
            var providerLabel = settings.EmbeddingProviderIds.Contains(provider.Id)
                ? $"embedding provider '{provider.Name}'"
                : $"model provider '{provider.Name}'";
            references.Add(providerLabel);
        }

        references.AddRange(GetSpeechReferences(speechSettings, credentialKey));
        return references;
    }

    /// <summary>Returns user-facing descriptions of all settings linked to a credential key.</summary>
    public static IReadOnlyList<string> GetReferences(
        ModelProviderSettings settings,
        SpeechSettings speechSettings,
        HookSettings hookSettings,
        string credentialKey)
    {
        var references = new List<string>(GetUserReferences(settings, speechSettings, credentialKey));
        references.AddRange(GetHookReferences(hookSettings, credentialKey));
        return references;
    }

    /// <summary>Returns user-facing descriptions of speech providers linked to a credential key.</summary>
    public static IReadOnlyList<string> GetSpeechReferences(SpeechSettings settings, string credentialKey)
    {
        var references = new List<string>();
        foreach (var provider in settings.RecognitionProviderConfigs
                     .Where(config => string.Equals(config.CredentialKey, credentialKey, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add($"speech recognition provider '{provider.Name}'");
        }

        foreach (var provider in settings.SynthesisProviderConfigs
                     .Where(config => string.Equals(config.CredentialKey, credentialKey, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add($"speech synthesis provider '{provider.Name}'");
        }

        return references;
    }

    /// <summary>Returns user-facing descriptions of hook headers linked to a credential key.</summary>
    public static IReadOnlyList<string> GetHookReferences(HookSettings hookSettings, string credentialKey)
    {
        var references = new List<string>();
        foreach (var hook in hookSettings.Hooks.Where(hook => hook.Http is not null))
        {
            foreach (var header in hook.Http!.SecretHeaders
                         .Where(header => string.Equals(header.CredentialKey, credentialKey, StringComparison.OrdinalIgnoreCase)))
            {
                references.Add($"hook '{hook.Name}' header '{header.Name}'");
            }
        }

        return references;
    }

    /// <summary>Moves model, embedding, and speech provider links to a renamed credential key.</summary>
    public static async Task<bool> RenameReferencesAsync(
        ModelProviderSettings settings,
        SpeechSettings speechSettings,
        string oldCredentialKey,
        string newCredentialKey)
    {
        var linkedProviders = settings.OwnedProviders
            .Where(config => string.Equals(config.CredentialKey, oldCredentialKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var linkedSpeechRecognitionProviders = speechSettings.RecognitionProviderConfigs
            .Where(config => string.Equals(config.CredentialKey, oldCredentialKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var linkedSpeechSynthesisProviders = speechSettings.SynthesisProviderConfigs
            .Where(config => string.Equals(config.CredentialKey, oldCredentialKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (linkedProviders.Count == 0 && linkedSpeechRecognitionProviders.Count == 0 && linkedSpeechSynthesisProviders.Count == 0)
        {
            return false;
        }

        foreach (var provider in linkedProviders)
        {
            provider.CredentialKey = newCredentialKey;
        }

        foreach (var provider in linkedSpeechRecognitionProviders.Concat(linkedSpeechSynthesisProviders))
        {
            provider.CredentialKey = newCredentialKey;
        }

        var hasModelReferences = linkedProviders.Count > 0;
        var hasSpeechReferences = linkedSpeechRecognitionProviders.Count > 0 || linkedSpeechSynthesisProviders.Count > 0;

        if (hasModelReferences && !await settings.TrySaveAsync().ConfigureAwait(false))
        {
            RestoreCredentialKeys(linkedProviders, linkedSpeechRecognitionProviders, linkedSpeechSynthesisProviders, oldCredentialKey);
            throw new CredentialReferenceRenameException(referencesRestored: true);
        }

        if (hasSpeechReferences && !await speechSettings.TrySaveAsync().ConfigureAwait(false))
        {
            RestoreCredentialKeys(linkedProviders, linkedSpeechRecognitionProviders, linkedSpeechSynthesisProviders, oldCredentialKey);
            var referencesRestored = !hasModelReferences || await settings.TrySaveAsync().ConfigureAwait(false);
            throw new CredentialReferenceRenameException(referencesRestored);
        }

        return true;
    }

    private static void RestoreCredentialKeys(
        IReadOnlyList<ModelProviderConfig> linkedProviders,
        IReadOnlyList<SpeechProviderConfig> linkedSpeechRecognitionProviders,
        IReadOnlyList<SpeechProviderConfig> linkedSpeechSynthesisProviders,
        string oldCredentialKey)
    {
        foreach (var provider in linkedProviders)
        {
            provider.CredentialKey = oldCredentialKey;
        }

        foreach (var provider in linkedSpeechRecognitionProviders.Concat(linkedSpeechSynthesisProviders))
        {
            provider.CredentialKey = oldCredentialKey;
        }
    }
}

/// <summary>Reports whether failed credential-reference persistence restored every previously saved link.</summary>
public sealed class CredentialReferenceRenameException(bool referencesRestored)
    : InvalidOperationException("Credential references could not be updated.")
{
    public bool ReferencesRestored { get; } = referencesRestored;
}
