namespace Oscova.Agents.Core.ApiContracts.CliAuth;

/// <summary>
/// Starts a CLI device login flow.
/// </summary>
public sealed class StartCliDeviceLoginRequest
{
    public string Client { get; set; } = "oscova-cli";
    public string Machine { get; set; } = string.Empty;
}

/// <summary>
/// Describes the user-facing device login challenge.
/// </summary>
public sealed class StartCliDeviceLoginResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; } = 2;
    public int ExpiresInSeconds { get; set; } = 600;
}

/// <summary>
/// Polls a CLI device login flow for completion.
/// </summary>
public sealed class PollCliDeviceLoginRequest
{
    public string DeviceCode { get; set; } = string.Empty;
}

/// <summary>
/// Returns the current state of a CLI device login flow.
/// </summary>
public sealed class PollCliDeviceLoginResponse
{
    public string Status { get; set; } = "pending";
    public string? Token { get; set; }
    public string? IdentityUserId { get; set; }
    public string? IdentityUserName { get; set; }
    public DateTimeOffset? IssuedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}