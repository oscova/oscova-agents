using System;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Oscova.Agents.Persistence;

/// <summary>
/// Serializes small Oscova file-format DTOs with the YAML shape used on disk.
/// </summary>
public sealed class YamlConverter
{
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDefaultScalarStyle(ScalarStyle.Plain)
        .DisableAliases()
        .Build();

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

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
}
