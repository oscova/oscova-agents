using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>
/// Centralizes the rule that chat selectors must not offer the catalog entries configured for embeddings.
/// </summary>
public static class ChatModelProviderSelection
{
    /// <summary>
    /// Builds selectable chat-provider options for screens that save agent or task overrides.
    /// </summary>
    public static IReadOnlyList<SelectableModelProvider> GetSelectableProviders(UserServiceScope userScope)
    {
        return [.. GetChatProviders(userScope.ModelProviderSettings.ModelProviderConfigs, userScope.ModelProviderSettings.EmbeddingProviderIds)
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new SelectableModelProvider
            {
                Id = provider.Id.ToString(),
                Name = provider.Name,
                ModelId = provider.ModelId
            })];
    }

    /// <summary>
    /// Gets the provider IDs that are valid for chat execution in the supplied user scope.
    /// </summary>
    public static IReadOnlySet<string> GetProviderIdSet(UserServiceScope userScope)
    {
        return GetProviderIdSet(
            userScope.ModelProviderSettings.ModelProviderConfigs,
            userScope.ModelProviderSettings.EmbeddingProviderIds);
    }

    /// <summary>
    /// Gets the provider IDs that are valid for chat execution from an explicit provider list.
    /// </summary>
    public static IReadOnlySet<string> GetProviderIdSet(
        IEnumerable<ModelProviderConfig> providers,
        IReadOnlyList<Guid> embeddingProviderIds)
    {
        return GetChatProviders(providers, embeddingProviderIds)
            .Select(provider => provider.Id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether a provider ID can be used as a chat provider override.
    /// </summary>
    public static bool ContainsProvider(UserServiceScope userScope, string? providerId)
    {
        return ContainsProvider(userScope.ModelProviderSettings, providerId);
    }

    /// <summary>
    /// Returns whether a provider ID can be used as a chat provider override.
    /// </summary>
    public static bool ContainsProvider(ModelProviderSettings settings, string? providerId)
    {
        var normalizedProviderId = NormalizeProviderId(providerId);
        return normalizedProviderId is not null &&
            GetProviderIdSet(settings.ModelProviderConfigs, settings.EmbeddingProviderIds)
                .Contains(normalizedProviderId);
    }

    /// <summary>Filters every embedding configuration out of a chat provider list, including inherited and unselected entries.</summary>
    public static IEnumerable<ModelProviderConfig> GetChatProviders(
        IEnumerable<ModelProviderConfig> providers,
        IReadOnlyList<Guid> embeddingProviderIds)
    {
        return providers.Where(provider =>
            !embeddingProviderIds.Contains(provider.Id));
    }

    private static string? NormalizeProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
    }
}
