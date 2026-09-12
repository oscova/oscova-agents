namespace Oscova.Agents.Secrets;

/// <summary>Wrapper written in place of a session document when the owning agent is confidential.</summary>
internal sealed class ProtectedSessionFile
{
    public bool IsEncrypted { get; set; }

    public string? ProtectedPayload { get; set; }
}
