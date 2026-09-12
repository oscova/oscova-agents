using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Settings;

/// <summary>Stores settings content by scope and settings key.</summary>
public interface IAppSettingsRepository
{
    /// <summary>Gets the content stored for a settings key.</summary>
    Task<string?> GetSettingsAsync(string scopeId, string settingsKey, CancellationToken ct = default);

    /// <summary>Saves content for a settings key.</summary>
    Task SaveSettingsAsync(string scopeId, string settingsKey, string content, CancellationToken ct = default);
}
