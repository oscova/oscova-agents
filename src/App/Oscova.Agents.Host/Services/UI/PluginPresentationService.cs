using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Channels;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>Resolves display assets declared by active plugins without exposing their deployment directories.</summary>
public sealed class PluginPresentationService(
    IAgentPluginCatalog plugins, ChannelCatalog channels, IStringLocalizerFactory localizers,
    ILogger<PluginPresentationService> logger)
{
    public string Text(LoadedAgentPlugin? plugin, string? key, string fallback)
    {
        if (plugin?.ResourceType is null || string.IsNullOrWhiteSpace(key)) { return fallback; }

        var text = localizers.Create(plugin.ResourceType)[key];
        return text.ResourceNotFound ? fallback : text.Value;
    }

    public string ChannelText(string channelId, string? key, string fallback)
        => Text(channels.Get(channelId).Plugin, key, fallback);

    public static string TopicKey(LoadedAgentPlugin plugin, PluginHelpTopic topic)
        => $"plugin:{plugin.Manifest.Id}:{topic.Id}";

    private static byte[] Read(LoadedAgentPlugin plugin, string resourceName, int limit)
    {
        using var stream = plugin.Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded resource '{resourceName}' is missing.");
        if (stream.Length > limit)
        {
            throw new InvalidDataException($"Embedded resource '{resourceName}' exceeds {limit} bytes.");
        }

        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }

    public string? Icon(string channelId)
    {
        var (plugin, contribution) = channels.Get(channelId);
        return Icon(plugin, contribution.Definition.IconResourceName);
    }

    /// <summary>Reads an optional icon from its declaring plugin using the shared resource limits.</summary>
    public string? Icon(LoadedAgentPlugin plugin, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }

        try
        {
            var bytes = Read(plugin, name, 65_536);
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 65_536
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != XName.Get("svg", "http://www.w3.org/2000/svg"))
            {
                throw new InvalidDataException("The declared icon must have an SVG root element.");
            }

            return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException)
        {
            logger.LogWarning(ex, "Plugin {PluginId} icon {ResourceName} could not be loaded.", plugin.Manifest.Id, name);
            plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                $"Icon '{name}' could not be loaded: {ex.Message}"));
            return null;
        }
    }

    public (LoadedAgentPlugin Plugin, ChannelDefinition? Channel, PluginHelpTopic Help)? FindHelp(string topicKey)
    {
        foreach (var channel in channels.Channels)
        {
            var plugin = channels.Get(channel.Id).Plugin;
            foreach (var help in Topics(channel))
            {
                if (string.Equals(TopicKey(plugin, help), topicKey, StringComparison.OrdinalIgnoreCase))
                {
                    return (plugin, channel, help);
                }
            }
        }

        foreach (var plugin in plugins.Plugins)
        {
            foreach (var help in plugin.SettingsPanels.SelectMany(Topics))
            {
                if (string.Equals(TopicKey(plugin, help), topicKey, StringComparison.OrdinalIgnoreCase)) { return (plugin, null, help); }
            }
        }

        return null;
    }

    public string ReadHelp(LoadedAgentPlugin plugin, PluginHelpTopic help)
    {
        var resource = help.ResourceName;
        for (var culture = CultureInfo.CurrentUICulture; culture != CultureInfo.InvariantCulture; culture = culture.Parent)
        {
            if (help.Translations.TryGetValue(culture.Name, out var translated))
            {
                resource = translated;
                break;
            }
        }

        return new UTF8Encoding(false, true).GetString(Read(plugin, resource, 524_288));
    }

    public string? ResolveSibling(LoadedAgentPlugin plugin, string filename)
    {
        var topic = plugin.Channels.SelectMany(item => Topics(item.Definition)).Concat(plugin.SettingsPanels.SelectMany(Topics)).FirstOrDefault(help =>
            string.Equals(help.Id + ".md", filename, StringComparison.OrdinalIgnoreCase));
        return topic is null ? $"plugin:{plugin.Manifest.Id}:{Path.GetFileNameWithoutExtension(filename)}" : TopicKey(plugin, topic);
    }

    private static IEnumerable<PluginHelpTopic> Topics(AgentSettingsContribution panel)
    {
        if (panel.Help is { } primary) { yield return primary; }

        foreach (var topic in panel.RelatedHelp) { yield return topic; }
    }

    private static IEnumerable<PluginHelpTopic> Topics(ChannelDefinition channel)
    {
        if (channel.Help is { } primary) { yield return primary; }

        foreach (var topic in channel.RelatedHelp) { yield return topic; }
    }
}
