using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Oscova.Agents.Core.ApiContracts.Mcp.Registry;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Dialogs.Settings;

/// <summary>
/// Lets users search the official MCP Registry and return an editor-ready server preset.
/// </summary>
public partial class McpRegistryBrowserDialog : ComponentBase
{
    [Inject]
    private ManagementApiClient ManagementApiClient { get; set; } = default!;

    [Inject]
    private ILogger<McpRegistryBrowserDialog> Logger { get; set; } = default!;

    [CascadingParameter]
    public DialogInstance DialogInstance { get; set; } = default!;

    private string _query = string.Empty;
    private bool _loading;
    private bool _searched;
    private List<McpRegistryCatalogEntry> _entries = [];
    private string? _error;

    private async Task SearchAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _searched = true;
        _error = null;

        try
        {
            var response = await ManagementApiClient.SearchMcpRegistryAsync(_query);
            _entries = [.. response.Entries.OrderBy(entry => entry.Title ?? entry.Name, StringComparer.OrdinalIgnoreCase)];
            _error = string.IsNullOrWhiteSpace(response.Error) ? null : response.Error;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to search the MCP registry.");
            _entries = [];
            _error = Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineSearchFailed, "MCP Registry search failed.");
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearchKeyDownAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Enter", StringComparison.Ordinal))
        {
            await SearchAsync();
        }
    }

    private void Choose(McpRegistryInstallOption option)
    {
        if (option.Server is null)
        {
            return;
        }

        DialogInstance.Close(DialogResult.Ok, option.Server);
    }

    private void Cancel()
    {
        DialogInstance.Close(DialogResult.Cancel);
    }

    private static string DisplayTitle(McpRegistryCatalogEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Title) ? entry.Name : entry.Title;

    private string DisplayOptionLabel(McpRegistryInstallOption option)
    {
        // Duplicate setup choices have numbered IDs, but their runtime command stays unchanged.
        return (option.Server?.Command ?? option.Kind) switch
        {
            var kind when kind.StartsWith("remote-http", StringComparison.OrdinalIgnoreCase) => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineRemoteHttpOption, "Remote (HTTP)"),
            var kind when kind.StartsWith("remote-sse", StringComparison.OrdinalIgnoreCase) => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineRemoteSseOption, "Remote (SSE)"),
            "npx" => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineNpxOption, "Local (npx)"),
            "uvx" => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineUvxOption, "Local (uvx)"),
            "docker" => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineDockerOption, "Local (docker)"),
            "dnx" => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineDnxOption, "Local (dnx)"),
            "manual" => Loc.GetOrDefault(SettingsTextKeys.McpServersFindOnlineManualOption, "Manual setup"),
            _ => string.IsNullOrWhiteSpace(option.Label) ? option.Kind : option.Label
        };
    }

    private string FormatSettingsText(string key, string fallback, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Loc.GetOrDefault(key, fallback), args);
    }
}
