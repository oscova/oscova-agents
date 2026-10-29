using System.Text.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.Nodes;

/// <summary>Names package preparation and tool invocation on an enrolled execution device.</summary>
public static class PluginCapability
{
    public const string Id = "plugins";
    public const int Version = 1;
    public const string Prepare = "prepare";
    public const string Invoke = "invoke";
    public static string ConfigurationKey(string pluginId) => "plugin:" + pluginId;
}

/// <summary>Identifies the exact approved archive without carrying account settings or secrets.</summary>
public sealed record NodePluginPackage(string PluginId, string PackageId, string Version);

/// <summary>Routes compatible plugin tools through the product-owned deployment and admission boundary.</summary>
public interface INodePluginExecutor
{
    /// <summary>Requires the selected execution plugin's exact package and successful device prerequisite check.</summary>
    void RequireExecutionReady(string userId, string nodeId, string? providerId);
    Task<JsonElement> InvokeAsync(string pluginId, RunContext run, ProjectDefinition project,
        AIFunction tool, AIFunctionArguments arguments, CancellationToken ct);
}
