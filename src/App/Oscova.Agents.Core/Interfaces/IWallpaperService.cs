using Oscova.Agents.Core.Users;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>Stores and serves the signed-in user's wallpaper and packaged wallpaper gallery.</summary>
public interface IWallpaperService
{
    /// <summary>Returns the packaged wallpaper names available to the gallery.</summary>
    IReadOnlyList<string> GetAvailableSeedNames();

    /// <summary>Opens a packaged wallpaper by name.</summary>
    ImageReadResult OpenSeedWallpaper(string seedName);

    /// <summary>Opens the user's wallpaper, seeding the default first when needed.</summary>
    ImageReadResult OpenUserWallpaper(string userId);

    /// <summary>Writes the user's uploaded wallpaper to its stable image path.</summary>
    Task WriteUserWallpaperAsync(string userId, Stream content, CancellationToken ct = default);

    /// <summary>Copies a packaged wallpaper over the user's current wallpaper.</summary>
    Task ApplyUserSeedAsync(string userId, string seedName, CancellationToken ct = default);
}
