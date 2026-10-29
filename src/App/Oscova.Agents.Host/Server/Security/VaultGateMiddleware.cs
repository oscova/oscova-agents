using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Oscova.Agents.Host.Server.Controllers;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Stops application requests until the vault and pending deletion are ready.</summary>
public sealed class VaultGateMiddleware(RequestDelegate next)
{
    /// <summary>Admits the vault gate and authentication while blocking locked application data.</summary>
    public async Task InvokeAsync(HttpContext context, IContentProtector protector, PendingDuressSelections pending,
        Oscova.Agents.Infrastructure.Users.OperatorVaultAccess operatorVault)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var isApplicationEndpoint = action is not null || context.Request.Path.StartsWithSegments("/agent") ||
            context.Request.Path.StartsWithSegments("/hubs/system");
        if (context.User.Identity?.IsAuthenticated == true && userId is not null && isApplicationEndpoint)
        {
            // Authentication and status remain usable while the operator is unlocking shared settings.
            bool isStatus = action?.ControllerTypeInfo.AsType() == typeof(ProfileController) &&
                action.ActionName == nameof(ProfileController.GetVaultStatus);
            bool isAuthentication = action?.ControllerTypeInfo.AsType() == typeof(AuthController);
            bool operatorReady = isAuthentication || isStatus ||
                await operatorVault.IsReadyAsync(userId, context.RequestAborted).ConfigureAwait(false);
            bool ownVaultReady = protector.IsAvailable(userId) && !pending.Contains(userId);
            if (!operatorReady || (!ownVaultReady && !IsGateEndpoint(action)))
            {
                // Browser page visits, such as an OAuth provider returning to its callback, go to the app's unlock screen.
                // Browsers mark navigations with this header, so API clients keep the 409 contract whatever they accept.
                if (context.Request.Headers["Sec-Fetch-Dest"] == "document")
                {
                    context.Response.Redirect("/");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = operatorReady ? "Vault locked" : "Waiting for the operator",
                    Detail = operatorReady ? "Unlock your Vault Password before continuing."
                        : "The operator must unlock their vault before you can continue."
                }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsGateEndpoint(ControllerActionDescriptor? action)
    {
        if (action?.ControllerTypeInfo.AsType() == typeof(AuthController)) { return true; }

        if (action?.ControllerTypeInfo.AsType() != typeof(ProfileController)) { return false; }

        return action.ActionName is nameof(ProfileController.GetVaultStatus) or
            nameof(ProfileController.SetVaultPassword) or nameof(ProfileController.UnlockVault)
#if DEBUG
            or nameof(ProfileController.DebugUnlock)
#endif
            ;
    }
}