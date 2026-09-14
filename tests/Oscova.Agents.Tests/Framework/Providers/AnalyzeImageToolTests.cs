using Microsoft.Extensions.AI;
using Oscova.Agents.Middleware;
using Oscova.Agents.Tools.Vision;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class AnalyzeImageToolTests : IDisposable
{
    private static readonly byte[] ImageBytes = [0x89, 0x50, 0x4E, 0x47];
    private readonly string _workspacePath;

    public AnalyzeImageToolTests()
    {
        _workspacePath = Path.Combine(
            Environment.CurrentDirectory,
            "analyze-image-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
    }

    [Fact]
    public void AnalyzeImageTool_AsAITool_ExposesAnalyzeImageByName()
    {
        var tool = new AnalyzeImageTool(new TestChatClient(), () => []).AsAITool();
        Assert.Equal(AnalyzeImageTool.ToolName, tool.Name);
    }

    [Fact]
    public void AnalyzeImageTool_WorkspaceValidation_RejectsEmptyAndOutsideGrants()
    {
        var emptyTool = new AnalyzeImageTool(new TestChatClient(), () => []).AsAITool();
        var emptyContext = CreateInvocationContext(emptyTool, Path.Combine(_workspacePath, "image.png"));
        Assert.Throws<UnauthorizedAccessException>(
            () => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(emptyContext, []));

        AllowedDirectory[] allowedDirectories =
        [
            new(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        var outsideTool = new AnalyzeImageTool(new TestChatClient(), () => allowedDirectories).AsAITool();
        var outsidePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "image.png");
        var outsideContext = CreateInvocationContext(outsideTool, outsidePath);

        Assert.Throws<UnauthorizedAccessException>(
            () => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(outsideContext, allowedDirectories));
    }

    [Theory]
    [InlineData("relative", WorkspaceAccessMode.ReadWrite)]
    [InlineData("sandbox", WorkspaceAccessMode.ReadWrite)]
    [InlineData("absolute", WorkspaceAccessMode.ReadOnly)]
    public async Task AnalyzeAsync_PermittedWorkspacePath_SendsImageBytes(
        string pathKind,
        WorkspaceAccessMode accessMode)
    {
        var hostPath = Path.Combine(_workspacePath, "image.png");
        await File.WriteAllBytesAsync(hostPath, ImageBytes);
        AllowedDirectory[] allowedDirectories =
        [
            new(
                _workspacePath,
                "default",
                DirectorySourceKind.LocalPath,
                accessMode,
                AgentPath: "/workspace/default")
        ];
        var requestedPath = pathKind switch
        {
            "relative" => "image.png",
            "sandbox" => "/workspace/default/image.png",
            _ => hostPath
        };
        var chatClient = new TestChatClient();
        var tool = new AnalyzeImageTool(chatClient, () => allowedDirectories).AsAITool();
        var context = CreateInvocationContext(tool, requestedPath);

        WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, allowedDirectories);
        await tool.InvokeAsync(context.Arguments);

        var imageContent = chatClient.ResponseMessages
            .Single()
            .Single()
            .Contents
            .OfType<DataContent>()
            .Single();
        Assert.Equal(ImageBytes, imageContent.Data.ToArray());
        Assert.Equal("image/png", imageContent.MediaType);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingFile_ReturnsNotFound()
    {
        var missing = Path.Combine(_workspacePath, "missing.png");
        var tool = new AnalyzeImageTool(new TestChatClient(), CreateWorkspaceResolver()).AsAITool();
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = missing,
            ["question"] = "what?"
        });
        Assert.Contains("not found", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_UnsupportedExtension_ReturnsUnsupported()
    {
        var path = Path.Combine(_workspacePath, "image.bmp");
        await File.WriteAllBytesAsync(path, [0x01, 0x02]);
        var tool = new AnalyzeImageTool(new TestChatClient(), CreateWorkspaceResolver()).AsAITool();
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = path,
            ["question"] = "what?"
        });
        Assert.Contains("unsupported", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private Func<IReadOnlyList<AllowedDirectory>> CreateWorkspaceResolver() => () =>
    [
        new(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
    ];

    private static FunctionInvocationContext CreateInvocationContext(AIFunction function, string path)
    {
        return new FunctionInvocationContext
        {
            Function = function,
            Arguments = new AIFunctionArguments
            {
                ["path"] = path,
                ["question"] = "what?"
            }
        };
    }
}
