using System.Reflection;
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

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
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
