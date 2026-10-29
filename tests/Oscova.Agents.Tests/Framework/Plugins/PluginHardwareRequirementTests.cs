using Oscova.Agents.Plugins;

namespace Oscova.Agents.Tests.Framework.Plugins;

public sealed class PluginHardwareRequirementTests
{
    [Theory]
    [InlineData("oscova-hardware.win-x64.cuda oscova-hardware.win-x64.cuda13", "cuda", 13)]
    [InlineData("oscova-hardware.win-x64.cpu oscova-hardware.linux-x64.cuda", "cpu", null)]
    [InlineData("oscova-hardware.win-x64.cuda13", null, null)]
    [InlineData("oscova-hardware.win-x64.cpu oscova-hardware.win-x64.cuda", null, null)]
    [InlineData("oscova-hardware.win-x64.cuda oscova-hardware.win-x64.cuda0", null, null)]
    [InlineData("oscova-hardware.win-x64.cuda oscova-hardware.win-x64.cuda2147483648", null, null)]
    [InlineData("oscova-hardware.win-x64.cuda oscova-hardware.win-x64.cuda13 oscova-hardware.win-x64.cuda12", null, null)]
    [InlineData("oscova-hardware.win-x64.vulkan", null, null)]
    [InlineData("oscova-hardware.win-x64.cpu oscova-hardware.win-x64.unknown", null, null)]
    [InlineData("local-models NVIDIA CUDA fast", null, null)]
    public void Tags_RequireOneConsistentDeclaration(string tags, string? backend, int? major)
    {
        var result = PluginHardwareRequirement.FromTags(tags.Split(' '), "win-x64");
        Assert.Equal(backend, result?.Backend);
        Assert.Equal(major, result?.CudaMajor);
    }
}
