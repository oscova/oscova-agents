using System;
using System.Collections.Generic;
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
        IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        MethodInfo? method = context.Function.UnderlyingMethod;
        if (method is null)
        {
            return;
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            WorkspacePathAttribute? workspaceAttribute = parameter.GetCustomAttribute<WorkspacePathAttribute>();
            if (workspaceAttribute is null || string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            string? path = GetPathArgument(parameter.Name, context.Arguments);
            ValidatePath(path, workspaceAttribute.Access, allowedDirectories);
        }
    }

    private static string? GetPathArgument(string parameterName, AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue(parameterName, out object? rawValue) || rawValue is null)
        {
            return null;
        }

        if (rawValue is string stringValue)
        {
            return stringValue;
        }

        if (rawValue is JsonElement jsonElement)
        {
            return jsonElement.ValueKind == JsonValueKind.Null
                ? null
                : jsonElement.GetString();
        }

        return rawValue.ToString();
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
