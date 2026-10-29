using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Infrastructure.Hooks;

/// <summary>
/// Creates one-time hook API-key tokens and validates inbound bearer tokens against stored hashes.
/// </summary>
internal sealed class HookApiKeyService(HookSettings settings, ILogger<HookApiKeyService> logger) : IHookApiKeyService
{
    private const string TokenPrefix = "ohk";
    private const string PersistenceFailureMessage = "Hook API key settings could not be saved.";
    private const int SecretByteCount = 32;
    private static readonly TimeSpan LastUsedAtWriteInterval = TimeSpan.FromMinutes(5);

    public async Task<HookApiKeyCreationResult> CreateAsync(HookApiKeyCreateOptions options, CancellationToken ct = default)
    {
        return await settings.RunExclusiveAsync(async () =>
        {
            await settings.LoadAsync().ConfigureAwait(false);
            var id = GenerateId();
            var secret = GenerateSecret();
            var plaintext = $"{TokenPrefix}_{id}.{secret}";
            var definition = new HookApiKeyDefinition
            {
                Id = id,
                Name = options.Name?.Trim() ?? string.Empty,
                TokenPrefix = plaintext[..Math.Min(16, plaintext.Length)],
                TokenHash = HashToken(plaintext),
                Scopes = options.Scopes.Count == 0 ? [HookSettingsValidator.TasksRunScope] : [.. options.Scopes],
                AllowedTaskSlugs = [.. options.AllowedTaskSlugs],
                CreatedAt = DateTimeOffset.UtcNow
            };

            settings.UpsertApiKey(definition);
            await settings.SaveAsync().ConfigureAwait(false);
            var normalized = settings.ApiKeys.Single(key => string.Equals(key.Id, id, StringComparison.OrdinalIgnoreCase));
            return new HookApiKeyCreationResult
            {
                Key = MapMetadata(normalized),
                PlaintextToken = plaintext
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HookApiKeyMetadata>> ListAsync(CancellationToken ct = default)
    {
        return await settings.RunExclusiveAsync(async () =>
        {
            await settings.LoadAsync().ConfigureAwait(false);
            return settings.ApiKeys.Select(MapMetadata).ToList();
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> SetEnabledAsync(string id, bool isEnabled, CancellationToken ct = default)
    {
        return await settings.RunExclusiveAsync(async () =>
        {
            await settings.LoadAsync().ConfigureAwait(false);
            var apiKeys = settings.ApiKeys.ToList();
            var apiKey = apiKeys.SingleOrDefault(key => string.Equals(key.Id, id, StringComparison.OrdinalIgnoreCase));
            if (apiKey is null)
            {
                return false;
            }

            if (apiKey.IsEnabled == isEnabled)
            {
                return true;
            }

            apiKey.IsEnabled = isEnabled;
            settings.ReplaceApiKeys(apiKeys);
            if (!await settings.TrySaveAsync().ConfigureAwait(false))
            {
                apiKey.IsEnabled = !isEnabled;
                settings.ReplaceApiKeys(apiKeys);
                throw new InvalidOperationException(PersistenceFailureMessage);
            }

            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        return await settings.RunExclusiveAsync(async () =>
        {
            await settings.LoadAsync().ConfigureAwait(false);
            var apiKeys = settings.ApiKeys.ToList();
            var deleted = settings.DeleteApiKey(id);
            if (!deleted)
            {
                return false;
            }

            if (!await settings.TrySaveAsync().ConfigureAwait(false))
            {
                settings.ReplaceApiKeys(apiKeys);
                throw new InvalidOperationException(PersistenceFailureMessage);
            }

            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task<HookApiKeyValidationResult> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new HookApiKeyValidationResult();
        }

        return await settings.RunExclusiveAsync(async () =>
        {
            await settings.LoadAsync().ConfigureAwait(false);
            var hashBytes = HashTokenBytes(token);
            foreach (var key in settings.ApiKeys.Where(key => key.IsEnabled))
            {
                if (FixedTimeEquals(hashBytes, key.TokenHash))
                {
                    var now = DateTimeOffset.UtcNow;
                    if (ShouldPersistLastUsedAt(key.LastUsedAt, now))
                    {
                        key.LastUsedAt = now;
                        _ = await settings.TrySaveAsync().ConfigureAwait(false);
                    }

                    return MapValid(key);
                }
            }

            logger.LogWarning("Rejected hook API key with prefix '{TokenPrefix}'.", SafePrefix(token));
            return new HookApiKeyValidationResult();
        }, ct).ConfigureAwait(false);
    }

    private static HookApiKeyMetadata MapMetadata(HookApiKeyDefinition key)
    {
        return new HookApiKeyMetadata
        {
            Id = key.Id,
            Name = key.Name,
            IsEnabled = key.IsEnabled,
            TokenPrefix = key.TokenPrefix,
            Scopes = [.. key.Scopes],
            AllowedTaskSlugs = [.. key.AllowedTaskSlugs],
            CreatedAt = key.CreatedAt,
            LastUsedAt = key.LastUsedAt
        };
    }

    private static HookApiKeyValidationResult MapValid(HookApiKeyDefinition key)
    {
        return new HookApiKeyValidationResult
        {
            IsValid = true,
            KeyId = key.Id,
            TokenPrefix = key.TokenPrefix,
            Scopes = key.Scopes.ToHashSet(StringComparer.OrdinalIgnoreCase),
            AllowedTaskSlugs = key.AllowedTaskSlugs.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string GenerateId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    }

    private static string GenerateSecret()
    {
        return EncodeBase64Url(RandomNumberGenerator.GetBytes(SecretByteCount));
    }

    private static string HashToken(string token)
    {
        return EncodeBase64Url(HashTokenBytes(token));
    }

    private static byte[] HashTokenBytes(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static bool FixedTimeEquals(byte[] leftBytes, string right)
    {
        try
        {
            var rightBytes = DecodeBase64Url(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool ShouldPersistLastUsedAt(DateTimeOffset? lastUsedAt, DateTimeOffset now)
    {
        return !lastUsedAt.HasValue || now - lastUsedAt.Value >= LastUsedAtWriteInterval;
    }

    private static string SafePrefix(string token)
    {
        return token[..Math.Min(16, token.Length)];
    }

    private static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
