using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Validates workspace-scoped tool arguments at function invocation time using the public
/// <see cref="FunctionInvokingChatClient"/> middleware hook.
/// </summary>
public static class WorkspacePathValidationMiddleware
{
    /// <summary>
    /// Adds per-invocation validation for tool parameters marked with <see cref="WorkspacePathAttribute"/>.
    /// </summary>
    public static ChatClientBuilder UseWorkspacePathValidation(
        this ChatClientBuilder builder,
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
    {
        return FunctionInvocationComposition.Contribute(builder, fic =>
        {
            var previousInvoker = fic.FunctionInvoker;
            fic.FunctionInvoker = async (context, ct) =>
            {
                // Resolve allowed directories at call time so agent setting changes are not cached in the pipeline.
                ValidateWorkspaceArguments(context, workspaceResolver());
                return previousInvoker is null
                    ? await context.Function.InvokeAsync(context.Arguments, ct).ConfigureAwait(false)
                    : await previousInvoker(context, ct).ConfigureAwait(false);
            };
        });
    }

    internal static void ValidateWorkspaceArguments(
        FunctionInvocationContext context,
        IReadOnlyList<AllowedDirectory> allowedDirectories) =>
        ValidateWorkspaceArguments(context.Function, context.Arguments, allowedDirectories);

    /// <summary>Validates a registered function's path arguments against the executing environment's grants.</summary>
    public static void ValidateWorkspaceArguments(
        AIFunction function,
        AIFunctionArguments arguments,
        IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        MethodInfo? method = function.UnderlyingMethod;
        if (method is null) { return; }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            WorkspacePathAttribute? workspaceAttribute = parameter.GetCustomAttribute<WorkspacePathAttribute>();
            if (workspaceAttribute is null || string.IsNullOrWhiteSpace(parameter.Name)) { continue; }
            foreach (string path in GetPathArguments(parameter.Name, arguments))
            {
                ValidatePath(path, workspaceAttribute.Access, allowedDirectories);
            }
        }
    }

    /// <summary>Reads the nonblank paths passed to a workspace path parameter, which may hold one path or an array of paths.</summary>
    public static IEnumerable<string> GetPathArguments(string parameterName, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue(parameterName, out object? rawValue) || rawValue is null)
        {
            return [];
        }

        IEnumerable<string?> paths = rawValue switch
        {
            string stringValue => [stringValue],
            JsonElement { ValueKind: JsonValueKind.Null } => [],
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(item => item.GetString()),
            JsonElement jsonElement => [jsonElement.GetString()],
            IEnumerable<string?> values => values,
            _ => [rawValue.ToString()]
        };
        return paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!);
    }

    private static void ValidatePath(
        string? path,
        WorkspacePathAccess requiredAccess,
        IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        PathHelper.ResolveAuthorizedHostPath(path, allowedDirectories, requiredAccess);
    }
}
