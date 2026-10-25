using Oscova.Agents.Policies;

namespace Oscova.Agents.Runtime;

/// <summary>
/// Carries source identity and network policy for a tool supplied by an external runtime.
/// Records store <c>ServerId</c>, the supplying server's stable id, instead of its display name.
/// </summary>
public sealed record ExternalToolMetadata(
    string ServerName,
    string OriginalName,
    NetworkScope NetworkScope = NetworkScope.None,
    string? ServerId = null);
