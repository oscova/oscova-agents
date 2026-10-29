namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>
/// Carries provider-owned editor defaults for an open provider key.
/// </summary>
public sealed record ProviderTypeDefault(
    string Key,
    string? DefaultEndpoint,
    string? DefaultModel,
    bool SupportsModelDownload = false);
