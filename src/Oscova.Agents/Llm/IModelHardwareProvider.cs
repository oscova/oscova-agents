namespace Oscova.Agents.Llm;

/// <summary>Describes requested placement without initializing a runtime or inspecting host hardware.</summary>
public interface IModelHardwareProvider
{
    /// <summary>Returns the backend and placement requested by this definition.</summary>
    ModelHardwareProfile GetHardwareProfile(ModelProviderConfig config);
}

/// <summary>Supplies requested backend, placement and optional layer count for host estimates.</summary>
public sealed record ModelHardwareProfile(string Backend, string Placement, int? RequestedGpuLayers = null);
