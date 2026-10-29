using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Oscova.Agents.Llm;

/// <summary>
/// Stores one configured language model provider entry.
/// </summary>
public class ModelProviderConfig
{
    /// <summary>
    /// Gets or sets the provider identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider type.
    /// </summary>
    public string Type { get; set; } = ModelProviderKeys.Ollama;

    /// <summary>
    /// Gets or sets the optional service base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets whether the endpoint is explicitly local; null infers from the resolved address.
    /// </summary>
    public bool? IsLocalEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the model identifier.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the credential lookup key.
    /// </summary>
    public string? CredentialKey { get; set; }

    /// <summary>Gets or sets non-secret, provider-owned configuration options.</summary>
    public IReadOnlyDictionary<string, string>? Options { get; set; }

    /// <summary>
    /// Gets or sets whether the provider supports image input.
    /// </summary>
    public bool SupportsImages { get; set; }

    /// <summary>
    /// Gets or sets the model context window size.
    /// </summary>
    public int ContextWindowSize { get; set; }

    /// <summary>
    /// Gets or sets the standard input price in USD per million tokens.
    /// </summary>
    public decimal? InputCostPerMillionTokens { get; set; }

    /// <summary>
    /// Gets or sets the standard output price in USD per million tokens.
    /// </summary>
    public decimal? OutputCostPerMillionTokens { get; set; }

    /// <summary>
    /// Gets or sets the Ollama model keep-alive duration to send with chat requests.
    /// </summary>
    public string? OllamaKeepAlive { get; set; }

    /// <summary>
    /// Gets or sets the chat options for the provider.
    /// </summary>
    public ChatOptions ChatOptions { get; set; } = new ChatOptions { Temperature = 0.7f };

    /// <summary>
    /// Gets or sets whether the provider supports reasoning output.
    /// </summary>
    public bool SupportsThinking { get; set; }

    /// <summary>
    /// Gets or sets the embedding vector dimensions.
    /// </summary>
    public int VectorDimensions { get; set; } = 768;

    /// <summary>
    /// Creates a copy of this provider configuration.
    /// </summary>
    public ModelProviderConfig Clone()
    {
        return new ModelProviderConfig
        {
            Id = Id,
            Name = Name,
            Type = Type,
            BaseUrl = BaseUrl,
            IsLocalEndpoint = IsLocalEndpoint,
            ModelId = ModelId,
            CredentialKey = CredentialKey,
            Options = Options is null ? null : new Dictionary<string, string>(Options, StringComparer.Ordinal),
            SupportsImages = SupportsImages,
            ContextWindowSize = ContextWindowSize,
            InputCostPerMillionTokens = InputCostPerMillionTokens,
            OutputCostPerMillionTokens = OutputCostPerMillionTokens,
            OllamaKeepAlive = OllamaKeepAlive,
            SupportsThinking = SupportsThinking,
            VectorDimensions = VectorDimensions,
            ChatOptions = ChatOptions?.Clone() ?? new ChatOptions(),
        };
    }

    /// <summary>
    /// Compares this configuration to another configuration instance.
    /// </summary>
    public bool EqualsConfiguration(ModelProviderConfig other)
    {
        return Id == other.Id
               && string.Equals(Type, other.Type, StringComparison.Ordinal)
               && string.Equals(Name, other.Name, StringComparison.Ordinal)
               && string.Equals(BaseUrl, other.BaseUrl, StringComparison.Ordinal)
               && IsLocalEndpoint == other.IsLocalEndpoint
               && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal)
               && string.Equals(CredentialKey, other.CredentialKey, StringComparison.Ordinal)
               && AreOptionsEqual(Options, other.Options)
               && SupportsImages == other.SupportsImages
               && ContextWindowSize == other.ContextWindowSize
               && InputCostPerMillionTokens == other.InputCostPerMillionTokens
               && OutputCostPerMillionTokens == other.OutputCostPerMillionTokens
               && string.Equals(OllamaKeepAlive, other.OllamaKeepAlive, StringComparison.Ordinal)
               && SupportsThinking == other.SupportsThinking
               && VectorDimensions == other.VectorDimensions
               && AreChatOptionsEqual(ChatOptions, other.ChatOptions);
    }

    private static bool AreChatOptionsEqual(ChatOptions? left, ChatOptions? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        // Serialize to compare all fields without manual property-by-property checks.
        var leftJson = JsonSerializer.Serialize(left);
        var rightJson = JsonSerializer.Serialize(right);

        return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
    }

    /// <summary>Compares exact option keys and values independently of order and dictionary comparers.</summary>
    public static bool AreOptionsEqual(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if ((left?.Count ?? 0) != (right?.Count ?? 0)) { return false; }
        if (left is null || left.Count == 0) { return true; }

        return left.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right!.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }
}
