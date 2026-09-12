using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oscova.Agents.Persistence;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Oscova.Agents.Skills;

/// <summary>
/// Normalizes SKILL.md frontmatter and owns Oscova agent-authored provenance keys.
/// </summary>
internal static class AgentAuthoredSkillFrontmatter
{
    internal const string AuthoredByValue = "agent";

    private const string MetadataKey = "metadata";
    private const string AuthoredByKey = "oscova-authored-by";
    private const string AuthorAgentIdKey = "oscova-author-agent-id";
    private const string AuthorAgentNameKey = "oscova-author-agent-name";
    private const string CreatedAtKey = "oscova-created-at";
    private const string UpdatedAtKey = "oscova-updated-at";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithDefaultScalarStyle(ScalarStyle.Plain)
        .DisableAliases()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <summary>
    /// Returns markdown with trusted Oscova provenance stamped for a new agent-authored skill.
    /// </summary>
    internal static AgentAuthoredSkillDocument NormalizeForCreate(
        string markdown,
        AgentAuthoredSkillContext context)
    {
        var document = Parse(markdown);
        StampReservedMetadata(document.Root, context, createdAt: context.TimestampUtc);
        return document.WithYaml(Serialize(document.Root));
    }

    /// <summary>
    /// Returns markdown with trusted Oscova provenance stamped for an existing agent-authored skill.
    /// </summary>
    internal static AgentAuthoredSkillDocument NormalizeForUpdate(
        string markdown,
        string expectedSkillName,
        AgentAuthoredSkillContext context,
        AgentAuthoredSkillProvenance existingProvenance)
    {
        var document = Parse(markdown);
        if (!string.Equals(document.Name, expectedSkillName, StringComparison.Ordinal))
        {
            throw new ArgumentException($"SKILL.md name must match '{expectedSkillName}'.");
        }

        // Preserve the original creation timestamp while proving this update came through Oscova.
        StampReservedMetadata(document.Root, context, existingProvenance.CreatedAtUtc);
        return document.WithYaml(Serialize(document.Root));
    }

    /// <summary>
    /// Reads trusted Oscova provenance from an installed SKILL.md file.
    /// </summary>
    internal static bool TryReadProvenance(
        string markdown,
        out AgentAuthoredSkillProvenance provenance)
    {
        provenance = default!;

        AgentAuthoredSkillDocument document;
        try
        {
            document = Parse(markdown);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or YamlException)
        {
            return false;
        }

        if (!TryGetMetadataMap(document.Root, out var metadata))
        {
            return false;
        }

        if (!string.Equals(GetScalar(metadata, AuthoredByKey), AuthoredByValue, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var createdAt = IsoTimestamp.Parse(GetScalar(metadata, CreatedAtKey) ?? string.Empty);
            var updatedAt = IsoTimestamp.Parse(GetScalar(metadata, UpdatedAtKey) ?? string.Empty);
            provenance = new AgentAuthoredSkillProvenance(createdAt, updatedAt);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rejects externally imported skills that try to claim Oscova-owned provenance.
    /// </summary>
    internal static void ThrowIfReservedMetadataPresent(string markdown)
    {
        var document = Parse(markdown);
        if (!TryGetMetadataMap(document.Root, out var metadata))
        {
            return;
        }

        if (metadata.Keys.Any(IsReservedKey))
        {
            throw new ArgumentException(
                "SKILL.md metadata uses reserved Oscova provenance keys. Remove metadata.oscova-* keys before importing this skill.");
        }
    }

    private static AgentAuthoredSkillDocument Parse(string markdown)
    {
        (string yaml, string body) = MarkdownFrontmatter.Split(markdown);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new ArgumentException("SKILL.md must contain YAML frontmatter delimited by '---'.");
        }

        IDictionary<object, object?>? rawRoot;
        try
        {
            rawRoot = Deserializer.Deserialize<Dictionary<object, object?>>(yaml);
        }
        catch (YamlException ex)
        {
            throw new ArgumentException("SKILL.md frontmatter is not valid YAML.", ex);
        }

        var root = NormalizeMap(rawRoot);
        var name = GetRequiredScalar(root, "name");
        var description = GetRequiredScalar(root, "description");

        return new AgentAuthoredSkillDocument(name, description, root, body, markdown);
    }

    private static string GetRequiredScalar(IDictionary<string, object?> map, string key)
    {
        var value = GetScalar(map, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"SKILL.md is missing a '{key}' field in frontmatter.");
        }

        return value;
    }

    private static string? GetScalar(IDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            DateTime timestamp => IsoTimestamp.FormatSeconds(new DateTimeOffset(
                timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : timestamp.ToUniversalTime())),
            DateTimeOffset timestamp => IsoTimestamp.FormatSeconds(timestamp),
            bool or int or long or double or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static void StampReservedMetadata(
        IDictionary<string, object?> root,
        AgentAuthoredSkillContext context,
        DateTimeOffset createdAt)
    {
        var metadata = GetOrCreateMetadataMap(root);

        // Model-provided Oscova keys are never trusted; the importer writes the canonical values.
        RemoveReservedKeys(metadata);
        metadata[AuthoredByKey] = AuthoredByValue;
        metadata[AuthorAgentIdKey] = context.AgentId;
        metadata[AuthorAgentNameKey] = context.AgentName;
        metadata[CreatedAtKey] = IsoTimestamp.FormatSeconds(createdAt.ToUniversalTime());
        metadata[UpdatedAtKey] = IsoTimestamp.FormatSeconds(context.TimestampUtc.ToUniversalTime());
        root[MetadataKey] = metadata;
    }

    private static Dictionary<string, object?> GetOrCreateMetadataMap(IDictionary<string, object?> root)
    {
        if (TryGetMetadataMap(root, out var metadata))
        {
            return metadata;
        }

        var replacement = new Dictionary<string, object?>(StringComparer.Ordinal);
        root[MetadataKey] = replacement;
        return replacement;
    }

    private static bool TryGetMetadataMap(
        IDictionary<string, object?> root,
        out Dictionary<string, object?> metadata)
    {
        if (root.TryGetValue(MetadataKey, out var value) &&
            value is Dictionary<string, object?> existing)
        {
            metadata = existing;
            return true;
        }

        metadata = [];
        return false;
    }

    private static void RemoveReservedKeys(Dictionary<string, object?> metadata)
    {
        foreach (string key in metadata.Keys
            .Where(IsReservedKey)
            .ToArray())
        {
            metadata.Remove(key);
        }
    }

    private static bool IsReservedKey(string key) =>
        key.StartsWith("oscova-", StringComparison.OrdinalIgnoreCase);

    private static string Serialize(IDictionary<string, object?> root) => Serializer.Serialize(root);

    private static Dictionary<string, object?> NormalizeMap(IDictionary<object, object?>? raw)
    {
        if (raw is null)
        {
            throw new ArgumentException("SKILL.md frontmatter must be a YAML mapping.");
        }

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (key is null)
            {
                continue;
            }

            normalized[Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty] = NormalizeValue(value);
        }

        return normalized;
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is IDictionary<object, object?> objectMap)
        {
            return NormalizeMap(objectMap);
        }

        if (value is IDictionary<string, object?> stringMap)
        {
            return new Dictionary<string, object?>(stringMap, StringComparer.Ordinal);
        }

        return value;
    }
}

/// <summary>
/// Holds normalized skill markdown plus frontmatter fields needed by import and authoring paths.
/// </summary>
internal sealed record AgentAuthoredSkillDocument(
    string Name,
    string Description,
    IDictionary<string, object?> Root,
    string Body,
    string Markdown)
{
    /// <summary>
    /// Recombines updated YAML with the original markdown body.
    /// </summary>
    internal AgentAuthoredSkillDocument WithYaml(string yaml) =>
        this with { Markdown = MarkdownFrontmatter.Combine(yaml, Body) };
}

/// <summary>
/// Captures provenance already stamped on an installed agent-authored skill.
/// </summary>
internal sealed record AgentAuthoredSkillProvenance(
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
