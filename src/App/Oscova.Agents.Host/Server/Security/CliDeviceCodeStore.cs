using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>
/// Tracks short-lived CLI device login grants between terminal polling and browser approval.
/// </summary>
public interface ICliDeviceCodeStore
{
    CliDeviceGrant Create(string client, string machine, Uri verificationBaseUri);
    CliDeviceGrant? FindByUserCode(string userCode);
    CliDeviceGrant? FindByDeviceCode(string deviceCode);
    /// <summary>Removes a device grant from both lookup keys.</summary>
    bool TryRemove(string deviceCode);
    bool Approve(string userCode, CliAccessTokenResult token);
    bool Deny(string userCode);
}

public sealed class CliDeviceCodeStore : ICliDeviceCodeStore
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, CliDeviceGrant> _grantsByDeviceCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _deviceCodeByUserCode = new(StringComparer.OrdinalIgnoreCase);

    public CliDeviceGrant Create(string client, string machine, Uri verificationBaseUri)
    {
        RemoveExpiredGrants();

        while (true)
        {
            var deviceCode = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string userCode;
            do
            {
                userCode = GenerateUserCode();
            }
            while (!_deviceCodeByUserCode.TryAdd(userCode, deviceCode));

            var verificationUri = new Uri(verificationBaseUri, $"cli-auth?code={Uri.EscapeDataString(userCode)}").ToString();
            var grant = new CliDeviceGrant
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                Client = string.IsNullOrWhiteSpace(client) ? "oscova-cli" : client.Trim(),
                Machine = machine?.Trim() ?? string.Empty,
                VerificationUri = verificationUri,
                ExpiresAtUtc = DateTimeOffset.UtcNow.Add(GrantLifetime)
            };

            if (_grantsByDeviceCode.TryAdd(deviceCode, grant))
            {
                return grant;
            }

            _deviceCodeByUserCode.TryRemove(userCode, out _);
        }
    }

    public CliDeviceGrant? FindByUserCode(string userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode) || !_deviceCodeByUserCode.TryGetValue(NormalizeUserCode(userCode), out var deviceCode))
        {
            return null;
        }

        return FindByDeviceCode(deviceCode);
    }

    public CliDeviceGrant? FindByDeviceCode(string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(deviceCode) || !_grantsByDeviceCode.TryGetValue(deviceCode, out var grant))
        {
            return null;
        }

        if (grant.IsExpired)
        {
            grant.Status = CliDeviceGrantStatus.Expired;
        }

        return grant;
    }

    /// <inheritdoc />
    public bool TryRemove(string deviceCode)
    {
        if (!_grantsByDeviceCode.TryRemove(deviceCode, out var grant)) return false;

        _deviceCodeByUserCode.TryRemove(grant.UserCode, out _);
        return true;
    }

    public bool Approve(string userCode, CliAccessTokenResult token)
    {
        var grant = FindByUserCode(userCode);
        if (grant?.IsExpired != false || grant.Status != CliDeviceGrantStatus.Pending)
        {
            return false;
        }

        grant.Token = token.Token;
        grant.IdentityUserId = token.UserId;
        grant.IdentityUserName = token.UserName;
        grant.IssuedAtUtc = token.IssuedAtUtc;
        grant.Status = CliDeviceGrantStatus.Approved;
        return true;
    }

    public bool Deny(string userCode)
    {
        var grant = FindByUserCode(userCode);
        if (grant?.IsExpired != false || grant.Status != CliDeviceGrantStatus.Pending)
        {
            return false;
        }

        grant.Status = CliDeviceGrantStatus.Denied;
        return true;
    }

    private void RemoveExpiredGrants()
    {
        foreach (var pair in _grantsByDeviceCode)
        {
            if (!pair.Value.IsExpired)
            {
                continue;
            }

            TryRemove(pair.Key);
        }
    }

    private static string GenerateUserCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[9];
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);

        for (var index = 0; index < bytes.Length; index++)
        {
            chars[index + (index >= 4 ? 1 : 0)] = alphabet[bytes[index] % alphabet.Length];
        }

        chars[4] = '-';
        return new string(chars);
    }

    public static string NormalizeUserCode(string userCode)
    {
        var compact = new string(userCode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == 8 ? $"{compact[..4]}-{compact[4..]}" : userCode.Trim().ToUpperInvariant();
    }
}

public sealed class CliDeviceGrant
{
    public string DeviceCode { get; init; } = string.Empty;
    public string UserCode { get; init; } = string.Empty;
    public string Client { get; init; } = string.Empty;
    public string Machine { get; init; } = string.Empty;
    public string VerificationUri { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public CliDeviceGrantStatus Status { get; set; } = CliDeviceGrantStatus.Pending;
    public string? Token { get; set; }
    public string? IdentityUserId { get; set; }
    public string? IdentityUserName { get; set; }
    public DateTimeOffset? IssuedAtUtc { get; set; }
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc;
}

public enum CliDeviceGrantStatus
{
    Pending,
    Approved,
    Denied,
    Expired
}
