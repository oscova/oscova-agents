using Oscova.Agents.Core;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Infrastructure.Utils;
using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>
/// File-backed store for user and agent profile pictures.
/// Pictures live under each owner's user root so avatars travel with the rest of that user's durable data.
/// Upload is an overwrite; missing pictures are copied from the deterministic seed pool.
/// </summary>
public sealed class ProfilePictureService(IAgentCatalog catalog) : IProfilePictureService
{
    private const string SeedFileExtension = ".png";

    public void EnsureUserPicture(string userId)
    {
        EnsureSeeded(GetUserFilePath(userId), userId);
    }

    public void EnsureAgentPicture(string agentId)
    {
        EnsureSeeded(GetAgentFilePath(agentId), agentId);
    }

    public Task WriteUserPictureAsync(string userId, Stream content, CancellationToken ct = default)
    {
        return SeededImageFileStore.WriteAsync(GetUserFilePath(userId), content, ct);
    }

    public Task WriteAgentPictureAsync(string agentId, Stream content, CancellationToken ct = default)
    {
        return SeededImageFileStore.WriteAsync(GetAgentFilePath(agentId), content, ct);
    }

    public IReadOnlyList<string> GetAvailableSeedNames()
    {
        return SeededImageFileStore.GetAvailableSeedNames(PathUtil.SeedProfilePicturesPath, SeedFileExtension);
    }

    public string? GetAssignedSeedName(string ownerId) => GetAvailableSeedNames().Count == 0 ? null : GetSeedName(ownerId);

    public ImageReadResult OpenSeedPicture(string seedName)
    {
        return SeededImageFileStore.Open(GetExistingSeedFilePath(seedName));
    }

    public Task ApplyUserSeedAsync(string userId, string seedName, CancellationToken ct = default)
    {
        SeededImageFileStore.CopySeed(GetExistingSeedFilePath(seedName), GetUserFilePath(userId));
        return Task.CompletedTask;
    }

    public Task ApplyAgentSeedAsync(string agentId, string seedName, CancellationToken ct = default)
    {
        SeededImageFileStore.CopySeed(GetExistingSeedFilePath(seedName), GetAgentFilePath(agentId));
        return Task.CompletedTask;
    }

    public ImageReadResult OpenUserPicture(string userId)
    {
        var filePath = GetUserFilePath(userId);
        EnsureSeeded(filePath, userId);
        return SeededImageFileStore.Open(filePath);
    }

    public ImageReadResult OpenAgentPicture(string agentId)
    {
        var filePath = GetAgentFilePath(agentId);
        EnsureSeeded(filePath, agentId);
        return SeededImageFileStore.Open(filePath);
    }

    private static void EnsureSeeded(string filePath, string seedKey)
    {
        // Stable seed selection prevents the profile picture from changing across restarts.
        SeededImageFileStore.EnsureSeeded(filePath, () => GetSeedFilePath(seedKey));
    }

    private static string GetSeedFilePath(string seedKey)
    {
        return GetExistingSeedFilePath(GetSeedName(seedKey));
    }

    private static string GetExistingSeedFilePath(string seedName)
    {
        return SeededImageFileStore.GetExistingSeedFilePath(
            PathUtil.SeedProfilePicturesPath,
            seedName,
            SeedFileExtension,
            "profile picture");
    }

    private static string GetSeedName(string seedKey)
    {
        var seedNames = SeededImageFileStore.GetAvailableSeedNames(
            PathUtil.SeedProfilePicturesPath,
            SeedFileExtension);
        if (seedNames.Count == 0)
        {
            throw new FileNotFoundException("No seed profile pictures were found.");
        }

        // Hash the stable user or agent id so reseeding picks the same named picture.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedKey));
        var value = BitConverter.ToUInt32(hash, 0);
        return seedNames[(int)(value % (uint)seedNames.Count)];
    }

    private static string GetUserFilePath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId, UserFileNames.ProfileImageFile);
    }

    private string GetAgentFilePath(string agentId)
    {
        ValidateAgentId(agentId);
        var agentDirectory = catalog.TryGetAgentDirectory(agentId)
            ?? throw new InvalidOperationException($"Unknown agentId '{agentId}'.");

        return Path.Combine(agentDirectory, UserFileNames.ProfileImageFile);
    }

    // Profile picture filenames are keyed by persisted GUID user ids.
    private static void ValidateUserId(string userId)
    {
        if (!Guid.TryParse(userId, out _))
        {
            throw new ArgumentException("Invalid user ID format.", nameof(userId));
        }
    }

    // All agents (including the per-user main agent) use GUID ids.
    private static void ValidateAgentId(string agentId)
    {
        if (!Guid.TryParse(agentId, out _))
        {
            throw new ArgumentException("Invalid agent ID format.", nameof(agentId));
        }
    }
}
