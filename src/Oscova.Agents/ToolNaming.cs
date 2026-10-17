using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Oscova.Agents;

/// <summary>
/// Provides helpers for constructing model-safe qualified tool names.
/// </summary>
public static partial class ToolNaming
{
    /// <summary>
    /// Separates model-safe qualified tool name segments.
    /// </summary>
    public const string WireSeparator = "__";

    /// <summary>
    /// Maximum tool name length accepted by model providers for function names.
    /// </summary>
    public const int MaxWireNameLength = 64;

    /// <summary>
    /// Sanitizes a name so it can safely be used as an AI function or tool name.
    /// Replaces any sequence of non-alphanumeric characters with an underscore.
    /// </summary>
    /// <param name="name">The name to sanitize, or <see langword="null"/>.</param>
    /// <returns>The sanitized name, or <see langword="null"/> if <paramref name="name"/> is <see langword="null"/>.</returns>
    public static string? SanitizeName(string? name)
        => name is null ? null : InvalidNameCharsRegex().Replace(name, "_").ToLowerInvariant();

    /// <summary>Regex that matches any sequence of characters that are not ASCII digits or letters.</summary>
    [GeneratedRegex("[^0-9A-Za-z]+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvalidNameCharsRegex();

    /// <summary>
    /// Normalizes a qualifier segment so it can safely be used as part of a tool name.
    /// </summary>
    /// <param name="qualifier">The qualifier value.</param>
    /// <returns>A normalized qualifier segment.</returns>
    public static string NormalizeQualifier(string qualifier)
    {
        if (string.IsNullOrWhiteSpace(qualifier))
        {
            return string.Empty;
        }

        return string.Join(WireSeparator, SplitQualifier(qualifier).Select(NormalizeSegment).Where(segment => segment.Length > 0));
    }

    /// <summary>
    /// Normalizes a tool name segment.
    /// </summary>
    /// <param name="toolName">The tool name.</param>
    /// <returns>The normalized tool name segment.</returns>
    public static string NormalizeToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return string.Empty;
        }

        return NormalizeSegment(toolName);
    }

    /// <summary>
    /// Builds a fully qualified tool wire name from the provided qualifier and base name.
    /// </summary>
    /// <param name="qualifier">The tool qualifier (toolset name, MCP server name, etc.).</param>
    /// <param name="baseName">The base tool name.</param>
    /// <returns>The fully qualified tool wire name.</returns>
    public static string BuildQualifiedName(string qualifier, string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        return BuildWireName([.. SplitQualifier(qualifier), baseName]);
    }

    /// <summary>
    /// Builds a qualified MCP tool wire name.
    /// </summary>
    public static string BuildMcpQualifiedName(string serverName, string toolName)
    {
        return BuildQualifiedName(serverName, toolName);
    }

    /// <summary>
    /// Builds a qualified reflected-toolset wire name.
    /// </summary>
    public static string BuildToolsetQualifiedName(string? origin, string owner, string toolName)
    {
        return string.IsNullOrWhiteSpace(origin)
            ? BuildWireName([owner, toolName])
            : BuildWireName([origin, owner, toolName]);
    }

    /// <summary>Builds the registered wire name for a public declared tool method.</summary>
    public static string BuildToolsetQualifiedName<TToolset>(string methodName, string? origin = null)
    {
        var type = typeof(TToolset);
        var method = type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new ArgumentException($"Public tool method '{methodName}' was not found on '{type.Name}'.", nameof(methodName));
        return BuildToolsetQualifiedName(origin, GetToolsetOwner(type), GetToolName(method));
    }

    /// <summary>Lists contributed tool identities without constructing a plugin's runtime.</summary>
    public static IReadOnlyList<string> GetToolsetQualifiedNames(Type type, string? origin = null)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.IsDefined(typeof(AgentToolAttribute), inherit: false))
            .Select(method => BuildToolsetQualifiedName(origin, GetToolsetOwner(type), GetToolName(method))).ToArray();

    /// <summary>Resolves method display names using the tool registration rules.</summary>
    internal static string GetToolName(MethodInfo method)
    {
        var displayName = method.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
        var baseName = string.IsNullOrWhiteSpace(displayName) ? method.Name : displayName;
        var toolName = NormalizeToolName(baseName);
        if (string.IsNullOrEmpty(toolName))
        {
            throw new InvalidOperationException($"Tool name derived from method '{method.Name}' cannot be empty.");
        }
        return toolName;
    }

    /// <summary>
    /// Returns the normalized friendly owner segment for a reflected toolset type.
    /// </summary>
    public static string GetToolsetOwner(Type toolsetType)
    {
        ArgumentNullException.ThrowIfNull(toolsetType);
        // A class display name opts into a readable owner without changing existing toolset names.
        string? displayName = toolsetType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
        var name = string.IsNullOrWhiteSpace(displayName)
            ? toolsetType.Name.EndsWith("Toolset", StringComparison.Ordinal)
                ? toolsetType.Name[..^"Toolset".Length]
                : toolsetType.Name
            : displayName;

        return NormalizeSegment(name);
    }

    /// <summary>
    /// Splits a fully qualified wire name into its qualifier and tool segments.
    /// </summary>
    /// <param name="qualifiedName">The fully qualified tool wire name.</param>
    /// <returns>A tuple containing the normalized qualifier and tool name segments.</returns>
    public static (string Qualifier, string Tool) SplitQualifiedName(string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);

        var segments = SplitQualifier(qualifiedName).Select(NormalizeSegment).Where(segment => segment.Length > 0).ToList();
        if (segments.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (segments.Count == 1)
        {
            return (string.Empty, segments[0]);
        }

        return (string.Join(WireSeparator, segments.Take(segments.Count - 1)), segments[^1]);
    }

    private static string BuildWireName(IReadOnlyList<string> segments)
    {
        var normalizedSegments = segments
            .SelectMany(SplitQualifier)
            .Select(NormalizeSegment)
            .Where(segment => segment.Length > 0)
            .ToArray();

        if (normalizedSegments.Length == 0)
        {
            throw new ArgumentException("Tool name cannot be empty after normalization.", nameof(segments));
        }

        var wireName = string.Join(WireSeparator, normalizedSegments);
        return wireName.Length <= MaxWireNameLength ? wireName : TruncateWithHash(wireName);
    }

    private static IEnumerable<string> SplitQualifier(string qualifier)
    {
        if (string.IsNullOrWhiteSpace(qualifier))
        {
            yield break;
        }

        foreach (var segment in qualifier.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var wireSegment in segment.Split(WireSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return wireSegment;
            }
        }
    }

    private static string TruncateWithHash(string wireName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wireName)))[..8].ToLowerInvariant();
        var suffix = WireSeparator + hash;
        var prefixLength = MaxWireNameLength - suffix.Length;
        var prefix = wireName[..prefixLength].TrimEnd('_', '-');
        if (prefix.Length == 0)
        {
            prefix = wireName[..prefixLength];
        }

        return prefix + suffix;
    }

    private static string NormalizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);
        var previousWasReplacement = false;
        foreach (var ch in segment.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                builder.Append(ch);
                previousWasReplacement = false;
            }
            else if (!previousWasReplacement)
            {
                builder.Append('_');
                previousWasReplacement = true;
            }
        }

        return builder.ToString().ToLowerInvariant();
    }
}
