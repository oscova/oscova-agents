using Oscova.Agents.Policies;

namespace Oscova.Agents.Runtime;

/// <summary>
/// Carries source identity and network policy for a tool supplied by an external runtime.
/// </summary>
public sealed record ExternalToolMetadata(
    string ServerName,
    string OriginalName,
    NetworkScope NetworkScope = NetworkScope.None);
