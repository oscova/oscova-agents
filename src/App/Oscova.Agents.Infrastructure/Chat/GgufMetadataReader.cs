using System.Text;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Reads GGUF attention metadata for a memory estimate without loading model weights.</summary>
public static class GgufMetadataReader
{
    private const uint Magic = 0x46554747;
    private const int MaxMetadataEntries = 4096;
    private const int MaxStringBytes = 1 << 20;
    private const long MaxArrayElements = 1 << 21;
    private const long MaxMetadataBytes = 64L << 20;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RetainedSuffixes =
    [
        "block_count",
        "attention.head_count",
        "attention.head_count_kv",
        "embedding_length",
        "attention.key_length",
        "attention.value_length"
    ];

    /// <summary>Returns the sizing for a GGUF file, or null when the file cannot be read or understood.</summary>
    public static ModelSizing? TryRead(string filePath, ILogger logger)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var weightBytes = stream.Length;
            using var reader = new BinaryReader(stream);

            if (!TryReadUInt32(reader, stream.Length, out var magic) || magic != Magic
                || !TryReadUInt32(reader, stream.Length, out var version) || version is < 2 or > 3
                || !TryReadUInt64(reader, stream.Length, out _)
                || !TryReadUInt64(reader, stream.Length, out var entryCount)
                || entryCount > MaxMetadataEntries)
            {
                return null;
            }

            var metadataEnd = Math.Min(stream.Length, checked(stream.Position + MaxMetadataBytes));
            string? architecture = null;
            var architectureSeen = false;
            var scalars = new Dictionary<string, int>(StringComparer.Ordinal);

            for (ulong index = 0; index < entryCount; index++)
            {
                if (!TryReadString(reader, metadataEnd, out var key)
                    || !TryReadUInt32(reader, metadataEnd, out var valueType))
                {
                    return null;
                }

                var isArchitecture = string.Equals(key, "general.architecture", StringComparison.Ordinal);
                var isScalar = IsRetainedScalarKey(key);
                if (isArchitecture)
                {
                    if (architectureSeen)
                    {
                        return null;
                    }

                    architectureSeen = true;
                }
                else if (isScalar && scalars.ContainsKey(key))
                {
                    return null;
                }

                if (IsIntegerType(valueType))
                {
                    if (!TryReadInteger(reader, valueType, metadataEnd, out var value))
                    {
                        return null;
                    }

                    if (isArchitecture)
                    {
                        architecture = null;
                    }
                    else if (isScalar)
                    {
                        scalars.Add(key, value ?? 0);
                    }

                    continue;
                }

                if (valueType == 8)
                {
                    if (!TryReadString(reader, metadataEnd, out var value))
                    {
                        return null;
                    }

                    if (isArchitecture)
                    {
                        architecture = value;
                    }
                    else if (isScalar)
                    {
                        scalars.Add(key, 0);
                    }

                    continue;
                }

                if (isScalar)
                {
                    scalars.Add(key, 0);
                }

                if (!TrySkipValue(reader, valueType, metadataEnd))
                {
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(architecture))
            {
                return null;
            }

            if (scalars.TryGetValue("split.count", out var splitCount)
                && (splitCount <= 0 || splitCount > 1))
            {
                return null;
            }

            var prefix = architecture + ".";
            var layers = GetScalar(scalars, prefix + "block_count");
            var heads = GetScalar(scalars, prefix + "attention.head_count");
            var kvHeads = GetScalar(scalars, prefix + "attention.head_count_kv");
            var embedding = GetScalar(scalars, prefix + "embedding_length");
            if (heads <= 0)
            {
                return null;
            }

            var keyLength = ModelFitEstimator.HeadDimension(
                GetOptionalScalar(scalars, prefix + "attention.key_length"),
                embedding,
                heads);
            var valueLength = ModelFitEstimator.HeadDimension(
                GetOptionalScalar(scalars, prefix + "attention.value_length"),
                embedding,
                heads);
            if (keyLength is null || valueLength is null)
            {
                return null;
            }

            var cachePerToken = ModelFitEstimator.KvCacheBytesPerToken(
                layers,
                kvHeads,
                keyLength.Value,
                valueLength.Value);
            return cachePerToken is { } cache ? new ModelSizing(weightBytes, cache) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            logger.LogWarning(ex, "Could not read model sizing from {ModelPath}", filePath);
            return null;
        }
    }

    private static bool IsRetainedScalarKey(string key)
    {
        return string.Equals(key, "split.count", StringComparison.Ordinal)
            || RetainedSuffixes.Any(suffix => key.EndsWith('.' + suffix, StringComparison.Ordinal));
    }

    private static int GetScalar(IReadOnlyDictionary<string, int> scalars, string key)
        => scalars.TryGetValue(key, out var value) ? value : 0;

    private static int? GetOptionalScalar(IReadOnlyDictionary<string, int> scalars, string key)
        => scalars.TryGetValue(key, out var value) ? value : null;

    private static bool IsIntegerType(uint valueType) => valueType is <= 5 or 10 or 11;

    private static bool TryReadInteger(BinaryReader reader, uint valueType, long end, out int? value)
    {
        value = null;
        try
        {
            switch (valueType)
            {
                case 0:
                    if (!CanRead(reader, 1, end)) return false;
                    value = reader.ReadByte();
                    return true;
                case 1:
                    if (!CanRead(reader, 1, end)) return false;
                    value = reader.ReadSByte();
                    return true;
                case 2:
                    if (!CanRead(reader, 2, end)) return false;
                    value = reader.ReadUInt16();
                    return true;
                case 3:
                    if (!CanRead(reader, 2, end)) return false;
                    value = reader.ReadInt16();
                    return true;
                case 4:
                    if (!CanRead(reader, 4, end)) return false;
                    var unsigned32 = reader.ReadUInt32();
                    value = unsigned32 <= int.MaxValue ? (int)unsigned32 : null;
                    return true;
                case 5:
                    if (!CanRead(reader, 4, end)) return false;
                    value = reader.ReadInt32();
                    return true;
                case 10:
                    if (!CanRead(reader, 8, end)) return false;
                    var unsigned64 = reader.ReadUInt64();
                    value = unsigned64 <= int.MaxValue ? (int)unsigned64 : null;
                    return true;
                case 11:
                    if (!CanRead(reader, 8, end)) return false;
                    var signed64 = reader.ReadInt64();
                    value = signed64 is >= int.MinValue and <= int.MaxValue ? (int)signed64 : null;
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            value = null;
            return true;
        }
    }

    private static bool TrySkipValue(BinaryReader reader, uint valueType, long end)
    {
        if (valueType == 9)
        {
            return TrySkipArray(reader, end);
        }

        var width = valueType switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0
        };
        return width > 0 && TrySkip(reader, width, end);
    }

    private static bool TrySkipArray(BinaryReader reader, long end)
    {
        if (!TryReadUInt32(reader, end, out var elementType)
            || elementType == 9
            || !TryReadUInt64(reader, end, out var count)
            || count > MaxArrayElements)
        {
            return false;
        }

        if (elementType == 8)
        {
            if (count > (ulong)((end - reader.BaseStream.Position) / sizeof(ulong)))
            {
                return false;
            }

            for (ulong index = 0; index < count; index++)
            {
                if (!TryReadString(reader, end, out _))
                {
                    return false;
                }
            }

            return true;
        }

        var width = elementType switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0
        };
        if (width == 0)
        {
            return false;
        }

        try
        {
            return TrySkip(reader, checked((long)count * width), end);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadString(BinaryReader reader, long end, out string value)
    {
        value = string.Empty;
        if (!TryReadUInt64(reader, end, out var byteCount) || byteCount > MaxStringBytes)
        {
            return false;
        }

        int length;
        try
        {
            length = checked((int)byteCount);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!CanRead(reader, length, end))
        {
            return false;
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadUInt32(BinaryReader reader, long end, out uint value)
    {
        value = 0;
        if (!CanRead(reader, sizeof(uint), end))
        {
            return false;
        }

        value = reader.ReadUInt32();
        return true;
    }

    private static bool TryReadUInt64(BinaryReader reader, long end, out ulong value)
    {
        value = 0;
        if (!CanRead(reader, sizeof(ulong), end))
        {
            return false;
        }

        value = reader.ReadUInt64();
        return true;
    }

    private static bool TrySkip(BinaryReader reader, long byteCount, long end)
    {
        if (!CanRead(reader, byteCount, end))
        {
            return false;
        }

        reader.BaseStream.Seek(byteCount, SeekOrigin.Current);
        return true;
    }

    private static bool CanRead(BinaryReader reader, long byteCount, long end)
    {
        return byteCount >= 0 && byteCount <= end - reader.BaseStream.Position;
    }
}
