using Oscova.Agents.Core.Hardware;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Whether a model fits in the memory of the computer running the host.</summary>
public enum ModelFitVerdict
{
    Fits,
    Tight,
    TooLarge,
    Unknown
}

/// <summary>The two model measurements a fit verdict needs.</summary>
public sealed record ModelSizing(long WeightBytes, long KvCacheBytesPerToken);

/// <summary>Compares estimated model memory with the host capacity budget.</summary>
public static class ModelFitEstimator
{
    private const double UsableVramFraction = 0.90;
    private const double UsableRamFraction = 0.75;
    private const double TightFraction = 0.85;

    /// <summary>Estimates weights plus F16 cache; overflowing metadata cannot produce a numeric footprint.</summary>
    public static long EstimateRequiredBytes(ModelSizing sizing, int contextTokens) =>
        checked(sizing.WeightBytes + sizing.KvCacheBytesPerToken * Math.Max(contextTokens, 0));

    /// <summary>Estimates F16 cache bytes per token, or null for invalid or overflowing metadata.</summary>
    public static long? KvCacheBytesPerToken(int layerCount, int kvHeadCount, int keyLength, int valueLength)
    {
        if (layerCount <= 0 || kvHeadCount <= 0 || keyLength <= 0 || valueLength <= 0)
        {
            return null;
        }

        try
        {
            return KvCacheBytesPerToken(
                checked((long)layerCount * kvHeadCount),
                keyLength,
                valueLength);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Estimates F16 cache bytes from the total KV heads across all layers.</summary>
    internal static long? KvCacheBytesPerToken(long kvHeadLayerTotal, int keyLength, int valueLength)
    {
        if (kvHeadLayerTotal <= 0 || keyLength <= 0 || valueLength <= 0)
        {
            return null;
        }

        try
        {
            return checked(kvHeadLayerTotal * ((long)keyLength + valueLength) * sizeof(short));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Uses an explicit dimension or an exact embedding-to-head ratio when absent.</summary>
    public static int? HeadDimension(int? declaredLength, int embeddingLength, int headCount)
    {
        if (declaredLength.HasValue)
        {
            return declaredLength > 0 ? declaredLength : null;
        }

        return embeddingLength > 0 && headCount > 0 && embeddingLength % headCount == 0
            ? embeddingLength / headCount
            : null;
    }

    /// <summary>Classifies complete positive model sizing against the host budget.</summary>
    public static ModelFitVerdict Estimate(ModelSizing sizing, int contextTokens, HostHardwareSnapshot hardware, ModelHardwareProfile profile)
    {
        if (sizing.WeightBytes <= 0 || sizing.KvCacheBytesPerToken <= 0) { return ModelFitVerdict.Unknown; }

        long? capacity = null;
        if (profile.Placement is "cpu" or "unified")
        {
            var budgets = new[] { hardware.PhysicalMemoryBytes, hardware.ProcessMemoryBudgetBytes }.Where(value => value > 0).ToArray();
            if (budgets.Length > 0) { capacity = (long)(budgets.Min()!.Value * UsableRamFraction); }
        }
        else if (profile.Backend == "cuda" && profile.Placement == "full-gpu" && hardware.DevicePlacementUnambiguous)
        {
            var devices = hardware.Adapters.Where(adapter => adapter.Vendor == "NVIDIA" && adapter.Source == "nvidia-smi").ToArray();
            if (devices.Length == 1 && devices[0].DedicatedMemoryBytes is > 0)
            { capacity = (long)(devices[0].DedicatedMemoryBytes!.Value * UsableVramFraction); }
        }

        if (capacity is null) { return ModelFitVerdict.Unknown; }

        long required;
        try
        {
            required = EstimateRequiredBytes(sizing, contextTokens);
        }
        catch (OverflowException)
        {
            return ModelFitVerdict.TooLarge;
        }

        if (required > capacity.Value)
        {
            return ModelFitVerdict.TooLarge;
        }

        return required > (long)(capacity.Value * TightFraction)
            ? ModelFitVerdict.Tight
            : ModelFitVerdict.Fits;
    }
}
