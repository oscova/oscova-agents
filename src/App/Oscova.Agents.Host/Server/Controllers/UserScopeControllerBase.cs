using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

public abstract class UserScopeControllerBase(UserServiceManager userServiceManager) : ControllerBase
{
    protected UserServiceManager UserServices => userServiceManager;

    protected void ApplyPrivateFileHeaders()
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
    }

    protected string GetUserId()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("User must be authenticated.");
        }

        return userId;
    }

    protected async Task<UserServiceScope> GetUserScopeAsync(CancellationToken ct = default)
    {
        return await GetUserScopeAsync(GetUserId(), ct);
    }

    protected async Task<UserServiceScope> GetUserScopeAsync(string userId, CancellationToken ct = default)
    {
        return await userServiceManager.GetOrCreateScopeAsync(userId, ct);
    }
}
