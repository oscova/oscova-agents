using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Stops authenticated conversation and task requests before locked storage is accessed.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class RequireUnlockedVaultAttribute : Attribute, IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var protector = context.HttpContext.RequestServices.GetRequiredService<IContentProtector>();
        if (!protector.IsAvailable(userId))
        {
            context.Result = new ConflictObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Vault locked",
                Detail = "Unlock your Vault Password before continuing."
            });
            return;
        }

        await next().ConfigureAwait(false);
    }
}
