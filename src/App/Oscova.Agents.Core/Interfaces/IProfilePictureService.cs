using Oscova.Agents.Core.Users;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Stores and serves profile picture blobs for users and agents.
/// Missing pictures are seeded deterministically so visible users and custom agents always have one.
/// </summary>
public interface IProfilePictureService
{
    /// <summary>
    /// Creates the user's seeded profile picture when no picture exists yet.
    /// </summary>
    void EnsureUserPicture(string userId);

    /// <summary>
    /// Creates the agent's seeded profile picture when no picture exists yet under its agent folder.
    /// </summary>
    void EnsureAgentPicture(string agentId);

    /// <summary>
    /// Writes (overwrites) the signed-in user's profile picture from <paramref name="content"/>.
    /// </summary>
    Task WriteUserPictureAsync(string userId, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Writes (overwrites) the agent's profile picture from <paramref name="content"/> under its agent folder.
    /// </summary>
    Task WriteAgentPictureAsync(string agentId, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Returns seed picture names that are currently present in the packaged seed pool.
    /// </summary>
    IReadOnlyList<string> GetAvailableSeedNames();

    /// <summary>
    /// Returns the seed picture name assigned to a user or agent id, the same picture its fallback uses,
    /// or null when no seed pictures are packaged.
    /// </summary>
    string? GetAssignedSeedName(string ownerId);

    /// <summary>
    /// Opens a seed picture by name for gallery preview.
    /// </summary>
    ImageReadResult OpenSeedPicture(string seedName);

    /// <summary>
    /// Copies the selected seed picture over the signed-in user's profile picture.
    /// </summary>
    Task ApplyUserSeedAsync(string userId, string seedName, CancellationToken ct = default);

    /// <summary>
    /// Copies the selected seed picture over the agent's profile picture under its agent folder.
    /// </summary>
    Task ApplyAgentSeedAsync(string agentId, string seedName, CancellationToken ct = default);

    /// <summary>
    /// Opens the user's profile picture, seeding it first if needed.
    /// </summary>
    ImageReadResult OpenUserPicture(string userId);

    /// <summary>
    /// Opens the agent's profile picture from its agent folder, seeding it first if needed.
    /// </summary>
    ImageReadResult OpenAgentPicture(string agentId);
}
