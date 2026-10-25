using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.Middleware;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Middleware;

public sealed class WorkspacePathValidationMiddlewareTests : IDisposable
{
    private readonly string _workspacePath;

    public WorkspacePathValidationMiddlewareTests()
    {
        _workspacePath = Path.Combine(
            Environment.CurrentDirectory,
            "workspace-validation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
    }

    [Fact]
    public void ValidateWorkspaceArguments_Allows_Path_Inside_Allowed_Directory()
    {
        string filePath = Path.Combine(_workspacePath, "notes.txt");

        var context = CreateInvocationContext(nameof(CodingToolset.ReadFile), "file_path", filePath);
        AllowedDirectory[] allowedDirectories =
        [
            new(_workspacePath, "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];

        WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, allowedDirectories);
    }

    [Fact]
    public void ValidateWorkspaceArguments_Rejects_AnnotatedPath_When_NoDirectoriesAreAllowed()
    {
        var context = CreateInvocationContext(nameof(CodingToolset.ReadFile), "file_path", _workspacePath);

        Assert.Throws<UnauthorizedAccessException>(
            () => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, []));
    }

    [Fact]
    public void ValidateWorkspaceArguments_Allows_UnannotatedArgument_When_NoDirectoriesAreAllowed()
    {
        var context = CreateInvocationContext(nameof(CodingToolset.ApplyPatch), "patch", _workspacePath);

        WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, []);
    }

    [Fact]
    public void ValidateWorkspaceArguments_Rejects_Path_Outside_Allowed_Directory()
    {
        string outsidePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "notes.txt");

        var context = CreateInvocationContext(nameof(CodingToolset.ReadFile), "file_path", outsidePath);
        AllowedDirectory[] allowedDirectories =
        [
            new(_workspacePath, "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];

        UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(
            () => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, allowedDirectories));

        Assert.Contains("outside the allowed workspace directories", exception.Message);
    }

    [Fact]
    public void ValidateWorkspaceArguments_Rejects_Write_To_ReadOnly_Directory()
    {
        string filePath = Path.Combine(_workspacePath, "notes.txt");

        var context = CreateInvocationContext(nameof(CodingToolset.WriteFile), "file_path", filePath);
        AllowedDirectory[] allowedDirectories =
        [
            new(_workspacePath, "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
        ];

        UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(
            () => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, allowedDirectories));

        Assert.Contains("read-only", exception.Message);
    }

    [Fact]
    public void ValidateWorkspaceArguments_EnforcesReadOnlyAccessThroughLogicalAlias()
    {
        var context = CreateInvocationContext(nameof(CodingToolset.WriteFile), "file_path", "mnt/reference/notes.txt");
        AllowedDirectory[] allowedDirectories =
        [
            new(_workspacePath, "reference", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, AgentPath: "/mnt/reference")
        ];

        UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(
            () => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(context, allowedDirectories));

        Assert.Contains("read-only", exception.Message);
        Assert.False(File.Exists(Path.Combine(_workspacePath, "notes.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    /// <summary>Direct executor validation uses only its supplied roots before performing IO.</summary>
    [Fact]
    public void DirectFunctionValidation_UsesOnlySuppliedExecutorGrants()
    {
        var read = CreateInvocationContext(nameof(CodingToolset.ReadFile), "file_path", Path.Combine(_workspacePath, "notes.txt"));
        AllowedDirectory[] readOnly = [new(_workspacePath, "reference", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)];
        WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(read.Function, read.Arguments, readOnly);
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(read.Function, read.Arguments, []));
        read.Arguments["file_path"] = Path.Combine(_workspacePath, "..", "outside.txt");
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(read.Function, read.Arguments, readOnly));
        var write = CreateInvocationContext(nameof(CodingToolset.WriteFile), "file_path", Path.Combine(_workspacePath, "notes.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(write.Function, write.Arguments, readOnly));
        Assert.False(File.Exists(Path.Combine(_workspacePath, "notes.txt")));
    }

    /// <summary>A path array, as a model sends it or as a direct caller passes it, yields every nonblank path.</summary>
    [Fact]
    public void GetPathArguments_ReadsEachPathOfAnArray()
    {
        Dictionary<string, object?> arguments = new()
        {
            ["json"] = JsonDocument.Parse("""["/shared/a.txt", "", "b.txt"]""").RootElement,
            ["array"] = new[] { "/shared/a.txt", " ", "b.txt" },
            ["single"] = "c.txt",
            ["missing"] = null
        };

        Assert.Equal(["/shared/a.txt", "b.txt"], WorkspacePathValidationMiddleware.GetPathArguments("json", arguments));
        Assert.Equal(["/shared/a.txt", "b.txt"], WorkspacePathValidationMiddleware.GetPathArguments("array", arguments));
        Assert.Equal(["c.txt"], WorkspacePathValidationMiddleware.GetPathArguments("single", arguments));
        Assert.Empty(WorkspacePathValidationMiddleware.GetPathArguments("missing", arguments));
    }

    private static FunctionInvocationContext CreateInvocationContext(
        string methodName,
        string argumentName,
        string path)
    {
        var toolset = new CodingToolset();
        MethodInfo method = typeof(CodingToolset).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' was not found.");

        var context = new FunctionInvocationContext
        {
            // The middleware uses UnderlyingMethod to find [WorkspacePath] parameters for the current tool call.
            Function = AIFunctionFactory.Create(method, target: toolset, options: new AIFunctionFactoryOptions { Name = method.Name }),
            Arguments = []
        };
        context.Arguments[argumentName] = path;
        return context;
    }
}
