namespace Oscova.Agents.Plugins.LlamaSharp;

/// <summary>Identifies the model settings resources supplied by this plugin.</summary>
public sealed class PluginText;

internal static class PluginTextKeys
{
    public const string OffloadMode = "Settings.LlamaSharp.OffloadMode";
    public const string FullOffload = "Settings.LlamaSharp.FullOffload";
    public const string CustomOffload = "Settings.LlamaSharp.CustomOffload";
    public const string GpuLayers = "Settings.LlamaSharp.GpuLayers";
    public const string GpuLayersHelp = "Settings.LlamaSharp.GpuLayersHelp";
    public const string GpuLayersInvalid = "Settings.LlamaSharp.GpuLayersInvalid";
    public const string CudaEditionRequired = "Settings.LlamaSharp.CudaEditionRequired";
}
