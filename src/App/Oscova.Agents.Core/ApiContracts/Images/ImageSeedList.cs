namespace Oscova.Agents.Core.ApiContracts.Images;

/// <summary>Available packaged image names for a gallery picker.</summary>
public sealed class ImageSeedList
{
    public IReadOnlyList<string> Seeds { get; init; } = [];
}
