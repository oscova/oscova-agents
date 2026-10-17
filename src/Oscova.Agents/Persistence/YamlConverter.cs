using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Oscova.Agents.Persistence;

/// <summary>
/// Serializes small Oscova file-format DTOs with the YAML shape used on disk.
/// </summary>
public sealed class YamlConverter
{
    private const string SchemaVersionKey = "schema_version";
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDefaultScalarStyle(ScalarStyle.Plain)
        .WithQuotingNecessaryStrings()
        .DisableAliases()
        .Build();

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
    private readonly IDeserializer _mappingDeserializer = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    /// <summary>Reads extensible metadata without discarding unknown fields or scalar value types.</summary>
    public IReadOnlyDictionary<string, object?> DeserializeMapping(string yaml) =>
        _mappingDeserializer.Deserialize<Dictionary<string, object?>>(yaml)
        ?? throw new InvalidDataException("Frontmatter must be a YAML mapping.");

    /// <summary>
    /// Serializes <paramref name="value"/> to YAML using snake_case keys.
    /// </summary>
    public string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _serializer.Serialize(value);
    }

    /// <summary>
    /// Deserializes YAML into <typeparamref name="T"/>, ignoring unknown fields for compatibility.
    /// </summary>
    public T Deserialize<T>(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        return _deserializer.Deserialize<T>(yaml);
    }

    /// <summary>Reads a document's schema marker, treating the original unmarked mapping as version one.</summary>
    public int ReadSchemaVersion(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) { throw new InvalidDataException("The document must contain a YAML mapping."); }
        Dictionary<string, object>? fields;
        try { fields = Deserialize<Dictionary<string, object>>(yaml); }
        catch (YamlException ex) { throw new InvalidDataException("The document schema marker could not be read.", ex); }
        if (fields is null) { throw new InvalidDataException("The document must contain a YAML mapping."); }
        if (!fields.TryGetValue(SchemaVersionKey, out var value)) { return 1; }
        if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidDataException("The document schema_version must be a positive integer.");
        }
        return version;
    }

    /// <summary>Rejects unsupported document schemas before binding their fields to the current model.</summary>
    public void RequireSchemaVersion(string yaml, int expectedVersion)
    {
        var actualVersion = ReadSchemaVersion(yaml);
        if (actualVersion != expectedVersion)
        {
            throw new InvalidDataException($"Document schema version {actualVersion} is unsupported. This application supports version {expectedVersion}.");
        }
    }

    /// <summary>Adds the owning domain's schema marker to its unmarked serialized settings snapshot.</summary>
    public static string WithSchemaVersion(string yaml, int version) =>
        $"{SchemaVersionKey}: {version.ToString(CultureInfo.InvariantCulture)}\n{yaml}";
}
