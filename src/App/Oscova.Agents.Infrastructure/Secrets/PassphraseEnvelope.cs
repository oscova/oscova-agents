using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>Seals a fixed-length payload under a passphrase using PBKDF2-SHA256 and AES-GCM.</summary>
internal static class PassphraseEnvelope
{
    public const int Version = 2;
    public const int Pbkdf2Iterations = 600_000;
    private const int LegacyPbkdf2Iterations = 600_000;
    public const int SaltLength = 16;
    public const int KeyLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Seals the payload and returns the serialized envelope.</summary>
    public static string Create(byte[] payload, string passphrase, byte[] associatedData)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var wrappingKey = DeriveKey(passphrase, salt, Pbkdf2Iterations);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[TagLength];
            var ciphertext = new byte[payload.Length];
            using var aes = new AesGcm(wrappingKey, TagLength);
            aes.Encrypt(nonce, payload, ciphertext, tag, associatedData);
            return JsonSerializer.Serialize(new Envelope
            {
                Version = Version,
                Iterations = Pbkdf2Iterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Payload = Convert.ToBase64String(ciphertext)
            }, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    /// <summary>Opens an envelope, returning null when authentication fails.</summary>
    public static byte[]? TryOpen(
        string serializedEnvelope,
        string passphrase,
        byte[] associatedData,
        int expectedPayloadLength)
    {
        var envelope = Parse(serializedEnvelope);
        var salt = DecodeExact(envelope.Salt, SaltLength, "salt");
        var nonce = DecodeExact(envelope.Nonce, NonceLength, "nonce");
        var tag = DecodeExact(envelope.Tag, TagLength, "tag");
        var ciphertext = DecodeExact(envelope.Payload, expectedPayloadLength, "payload");
        // V1 omitted the cost, so its original profile must remain independent of new writes.
        var iterations = envelope.Version == 1 ? LegacyPbkdf2Iterations : envelope.Iterations;
        var wrappingKey = DeriveKey(passphrase, salt, iterations);
        var payload = new byte[expectedPayloadLength];
        try
        {
            using var aes = new AesGcm(wrappingKey, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, payload, associatedData);
            return payload;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(payload);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passphraseBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static Envelope Parse(string serializedEnvelope)
    {
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(serializedEnvelope, JsonOptions)
                ?? throw new InvalidOperationException("The passphrase envelope is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The passphrase envelope is invalid.", ex);
        }

        if (envelope.Version is not (1 or Version))
        {
            throw new InvalidOperationException("The passphrase envelope version is not supported.");
        }

        if (envelope.Version == Version &&
            (envelope.Iterations < LegacyPbkdf2Iterations || envelope.Iterations > Pbkdf2Iterations))
        {
            throw new InvalidOperationException("The passphrase envelope iteration count is not supported.");
        }

        return envelope;
    }

    private static byte[] DecodeExact(string? value, int length, string fieldName)
    {
        if (value is null)
        {
            throw new InvalidOperationException($"The passphrase envelope {fieldName} is invalid.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"The passphrase envelope {fieldName} is invalid.", ex);
        }

        if (bytes.Length != length)
        {
            throw new InvalidOperationException($"The passphrase envelope {fieldName} has an invalid length.");
        }

        return bytes;
    }

    private sealed class Envelope
    {
        public int Version { get; init; }
        public int Iterations { get; init; }
        public string Salt { get; init; } = string.Empty;
        public string Nonce { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
    }
}
