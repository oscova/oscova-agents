using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Speech;

/// <summary>Tracks admitted speech providers so compiled settings can only target their owning plugin.</summary>
public sealed class SpeechProviderOwnership
{
    private readonly Dictionary<string, LoadedAgentPlugin> _recognition = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedAgentPlugin> _synthesis = new(StringComparer.Ordinal);

    /// <summary>Records ownership after a recognition provider is admitted.</summary>
    public void AddRecognition(string type, LoadedAgentPlugin plugin) => _recognition.Add(type, plugin);
    /// <summary>Records ownership after a synthesis provider is admitted.</summary>
    public void AddSynthesis(string type, LoadedAgentPlugin plugin) => _synthesis.Add(type, plugin);
    /// <summary>Finds the owner of an admitted recognition provider.</summary>
    public LoadedAgentPlugin? RecognitionOwner(string type) => _recognition.GetValueOrDefault(type);
    /// <summary>Finds the owner of an admitted synthesis provider.</summary>
    public LoadedAgentPlugin? SynthesisOwner(string type) => _synthesis.GetValueOrDefault(type);
}
