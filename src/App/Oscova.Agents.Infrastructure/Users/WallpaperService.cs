using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>Stores each user's wallpaper beside their other durable user-owned files.</summary>
public sealed class WallpaperService : IWallpaperService
{
    private const string DefaultSeedName = "ember-valley";
    private const string SeedFileExtension = ".webp";

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableSeedNames()
    {
        return SeededImageFileStore.GetAvailableSeedNames(PathUtil.SeedWallpapersPath, SeedFileExtension);
    }

    /// <inheritdoc />
    public ImageReadResult OpenSeedWallpaper(string seedName)
    {
        return SeededImageFileStore.Open(GetExistingSeedFilePath(seedName));
    }

    /// <inheritdoc />
    public ImageReadResult OpenUserWallpaper(string userId)
    {
        var userFilePath = GetUserFilePath(userId);
        SeededImageFileStore.EnsureSeeded(userFilePath, () => GetExistingSeedFilePath(DefaultSeedName));
        return SeededImageFileStore.Open(userFilePath);
    }

    /// <inheritdoc />
    public Task WriteUserWallpaperAsync(string userId, Stream content, CancellationToken ct = default)
    {
        return SeededImageFileStore.WriteAsync(GetUserFilePath(userId), content, ct);
    }

    /// <inheritdoc />
    public Task ApplyUserSeedAsync(string userId, string seedName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SeededImageFileStore.CopySeed(GetExistingSeedFilePath(seedName), GetUserFilePath(userId));
        return Task.CompletedTask;
    }

    private static string GetExistingSeedFilePath(string seedName)
    {
        return SeededImageFileStore.GetExistingSeedFilePath(
            PathUtil.SeedWallpapersPath,
            seedName,
            SeedFileExtension,
            "wallpaper");
    }

    private static string GetUserFilePath(string userId)
    {
        if (!Guid.TryParse(userId, out _))
        {
            throw new ArgumentException("Invalid user ID format.", nameof(userId));
        }

        return Path.Combine(PathUtil.UsersPath, userId, "wallpaper.image");
    }
}
