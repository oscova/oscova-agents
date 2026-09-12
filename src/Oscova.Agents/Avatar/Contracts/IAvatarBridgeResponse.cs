namespace Oscova.Agents.Avatar.Contracts;

/// <summary>
/// Common result shape returned by Avatar Engine bridge commands.
/// </summary>
public interface IAvatarBridgeResponse
{
    /// <summary>
    /// Gets whether the Avatar Engine processed the command successfully.
    /// </summary>
    bool Success { get; }

    /// <summary>
    /// Gets the human-readable operation result.
    /// </summary>
    string Description { get; }
}
