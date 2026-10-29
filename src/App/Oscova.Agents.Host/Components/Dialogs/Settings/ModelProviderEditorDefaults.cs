using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Host.Components.Dialogs.Settings;

/// <summary>
/// Resolves shared provider editor defaults used by chat and embedding provider dialogs.
/// </summary>
internal static class ModelProviderEditorDefaults
{
    private static readonly IReadOnlyDictionary<string, ModelProviderConfig> PresetsByType =
        ModelProviderConfigPresets.GetPresets()
            .GroupBy(preset => preset.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Clone(), StringComparer.Ordinal);

    internal static IReadOnlyList<string> BuiltInProviderTypes =>
        [.. PresetsByType.Keys.OrderBy(type => type, StringComparer.Ordinal)];

    internal static bool TryGetPreset(string type, out ModelProviderConfig preset)
    {
        if (PresetsByType.TryGetValue(type, out var foundPreset))
        {
            preset = foundPreset.Clone();
            return true;
        }

        preset = new ModelProviderConfig();
        return false;
    }

    /// <summary>Copies the context window and capability flags a fetched model reports, returning whether any were present.</summary>
    internal static bool ApplyModelMetadata(ModelProviderConfig config, ModelDescriptor? model)
    {
        var applied = false;
        if (model?.ContextWindowSize is int contextWindow)
        {
            config.ContextWindowSize = contextWindow;
            applied = true;
        }

        if (model?.SupportsImages is bool supportsImages)
        {
            config.SupportsImages = supportsImages;
            applied = true;
        }

        if (model?.SupportsThinking is bool supportsThinking)
        {
            config.SupportsThinking = supportsThinking;
            applied = true;
        }

        return applied;
    }

    internal static string GetChatProviderName(string type)
    {
        return PresetsByType.TryGetValue(type, out var preset)
            ? preset.Name
            : type;
    }

    internal static string GetEmbeddingProviderName(string type)
    {
        var baseName = GetChatProviderName(type);
        return $"{baseName} Embeddings";
    }

    internal static string? GetDefaultEndpoint(
        string type,
        IEnumerable<ProviderTypeDefault> providerTypeDefaults)
    {
        var providerDefault = providerTypeDefaults.FirstOrDefault(
            defaultValue => string.Equals(defaultValue.Key, type, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(providerDefault?.DefaultEndpoint))
        {
            return providerDefault.DefaultEndpoint;
        }

        return PresetsByType.TryGetValue(type, out var preset)
            ? preset.BaseUrl
            : null;
    }
}
