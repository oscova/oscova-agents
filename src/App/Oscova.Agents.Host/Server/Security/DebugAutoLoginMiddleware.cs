#if DEBUG
using System.Net;
using System.Security.Claims;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Host.Utils;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Resumes local development sessions through the normal credential and vault endpoints.</summary>
public sealed class DebugAutoLoginMiddleware(RequestDelegate next)
{
    /// <summary>Redirects local page loads when the configured development session needs unlocking.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsAllowed(context) && HttpMethods.IsGet(context.Request.Method) &&
            (context.Request.Path == "/" || context.Request.Path == "/login" || context.Request.Path.StartsWithSegments("/settings")))
        {
            var username = context.RequestServices.GetRequiredService<IConfiguration>()["DebugAutoLogin:Username"];
            var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByNameAsync(username!);
            var vault = context.RequestServices.GetRequiredService<IVaultDataProtector>();
            // Only a fresh environment can create the configured development operator.
            if (user is null
                ? await userManager.Users.AnyAsync(account => account.Id != WellKnownUsers.SystemId, context.RequestAborted)
                : !(await vault.GetStateAsync(user.Id, context.RequestAborted)).IsConfigured)
            {
                await next(context);
                return;
            }

            var destination = context.Request.Path == "/login"
                ? LoginNavigation.LocalReturnUrl(context.Request.Query["ReturnUrl"])
                : context.Request.Path + context.Request.QueryString;
            string? endpoint = null;
            if (context.User.Identity?.IsAuthenticated != true)
            {
                endpoint = "/auth/debug-login";
            }
            else if (string.Equals(context.User.Identity.Name,
                         username, StringComparison.OrdinalIgnoreCase))
            {
                var state = await vault.GetStateAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, context.RequestAborted);
                if (!state.IsUnlocked) { endpoint = "/api/profile/debug-unlock"; }
            }

            if (endpoint is not null)
            {
                context.Response.Redirect(QueryHelpers.AddQueryString(endpoint, "returnUrl", destination));
                return;
            }
        }

        await next(context);
    }

    /// <summary>Limits automatic credentials to explicitly enabled, direct local development requests.</summary>
    internal static bool IsAllowed(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var address = context.Connection.RemoteIpAddress;
        var host = context.Request.Host.Host;
        return context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() &&
               configuration.GetValue<bool>("DebugAutoLogin:Enabled") &&
               address is not null && IPAddress.IsLoopback(address) &&
               (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(host.Trim('[', ']'), out var hostAddress) && IPAddress.IsLoopback(hostAddress)) &&
               !string.IsNullOrWhiteSpace(configuration["DebugAutoLogin:Username"]) &&
               !string.IsNullOrWhiteSpace(configuration["DebugAutoLogin:Password"]) &&
               !string.IsNullOrWhiteSpace(configuration["DebugAutoLogin:VaultPassword"]);
    }
}
#endif
