using System.Security.Cryptography;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>
/// Holds the per-process secret that authorizes same-machine CLI lifecycle control.
/// </summary>
public sealed class LocalControlTokenProvider
{
    /// <summary>
    /// Gets the per-start local control secret written to the runtime descriptor.
    /// </summary>
    public string Token { get; } = RandomNumberGenerator.GetHexString(64);
}
