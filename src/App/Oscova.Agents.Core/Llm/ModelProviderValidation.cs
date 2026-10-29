using System.Text;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.Llm;

/// <summary>Applies shared option limits before provider-owned validation at save and execution boundaries.</summary>
public static class ModelProviderValidation
{
    /// <summary>Checks the non-secret option payload and preserves unchanged opaque options when a plugin is unavailable.</summary>
    public static IReadOnlyDictionary<string, string> Validate(ModelProviderConfig config, IModelProvider? provider,
        ModelProviderConfig? previous = null)
    {
        long bytes = 0;
        foreach (var pair in config.Options ?? new Dictionary<string, string>())
        {
            if (pair.Value is null) { return new Dictionary<string, string> { [nameof(config.Options)] = "Option values must be strings." }; }

            bytes += Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value);
            if (bytes > 16 * 1024)
            {
                return new Dictionary<string, string> { [nameof(config.Options)] = "Option keys and values must total at most 16 KiB of UTF-8 text." };
            }
        }

        if (provider is IModelProviderConfiguration configuration) { return configuration.ValidateConfiguration(config); }

        if (provider is null && previous is not null && config.Type == previous.Type)
        {
            if (ModelProviderConfig.AreOptionsEqual(config.Options, previous.Options)) { return new Dictionary<string, string>(); }
        }
        else if (config.Options is null or { Count: 0 }) { return new Dictionary<string, string>(); }

        return new Dictionary<string, string> { [nameof(config.Options)] = "Options cannot be changed until a provider supporting them is available." };
    }
}
