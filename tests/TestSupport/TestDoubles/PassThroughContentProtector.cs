using System.Collections.Concurrent;
using System.Text;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Tests.Infrastructure.TestDoubles;

/// <summary>Records protection calls and verifies that reads use the same user and purpose.</summary>
public sealed class PassThroughContentProtector : IContentProtector
{
    private readonly ConcurrentQueue<(string UserId, string Purpose)> _protectCalls = [];
    private readonly ConcurrentQueue<(string UserId, string Purpose)> _unprotectCalls = [];

    public IReadOnlyList<(string UserId, string Purpose)> ProtectCalls => [.. _protectCalls];
    public IReadOnlyList<(string UserId, string Purpose)> UnprotectCalls => [.. _unprotectCalls];
    public bool Available { get; set; } = true;

    public bool IsAvailable(string userId) => Available;

    public string Protect(string userId, string purpose, string plaintext)
    {
        _protectCalls.Enqueue((userId, purpose));
        return $"{userId.Length}:{userId}{purpose.Length}:{purpose}" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
    }

    public string? Unprotect(string userId, string purpose, string ciphertext)
    {
        _unprotectCalls.Enqueue((userId, purpose));
        var prefix = $"{userId.Length}:{userId}{purpose.Length}:{purpose}";
        if (!ciphertext.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[prefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
