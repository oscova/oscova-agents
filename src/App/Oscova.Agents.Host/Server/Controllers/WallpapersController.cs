using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Images;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Serves packaged wallpapers and the signed-in user's wallpaper.</summary>
[ApiController]
[Authorize]
[Route("api/wallpapers")]
[IgnoreAntiforgeryToken]
public sealed class WallpapersController(
    UserServiceManager userServiceManager,
    IWallpaperService wallpapers)
    : UserScopeControllerBase(userServiceManager)
{
    /// <summary>Returns the packaged wallpaper names available to the gallery.</summary>
    [HttpGet("seeds")]
    public IActionResult GetSeeds()
    {
        return Ok(new ImageSeedList { Seeds = wallpapers.GetAvailableSeedNames() });
    }

    /// <summary>Streams a packaged wallpaper for gallery preview.</summary>
    [HttpGet("seeds/{seedName}")]
    public IActionResult GetSeedWallpaper(string seedName)
    {
        try
        {
            return Stream(wallpapers.OpenSeedWallpaper(seedName));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Streams the signed-in user's wallpaper, seeding the default when needed.</summary>
    [HttpGet("user")]
    public IActionResult GetUserWallpaper()
    {
        return Stream(wallpapers.OpenUserWallpaper(GetUserId()));
    }

    /// <summary>Replaces the signed-in user's wallpaper with an uploaded image.</summary>
    [HttpPost("user")]
    [RequestSizeLimit(WallpaperContent.MaxRequestSizeBytes)]
    public async Task<IActionResult> UploadUserWallpaper(IFormFile file, CancellationToken ct)
    {
        if (!ImageUploadValidation.TryValidate(file, WallpaperContent.MaxUploadSizeBytes, "Wallpaper", out var error))
        {
            return BadRequest(new { message = error });
        }

        await using var stream = file.OpenReadStream();
        await wallpapers.WriteUserWallpaperAsync(GetUserId(), stream, ct);
        return NoContent();
    }

    /// <summary>Applies a packaged wallpaper to the signed-in user.</summary>
    [HttpPost("user/seed/{seedName}")]
    public async Task<IActionResult> ApplyUserSeedWallpaper(string seedName, CancellationToken ct)
    {
        try
        {
            await wallpapers.ApplyUserSeedAsync(GetUserId(), seedName, ct);
            return NoContent();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    private IActionResult Stream(ImageReadResult result)
    {
        Response.Headers.CacheControl = "no-store";
        return File(result.Stream, result.ContentType);
    }
}
