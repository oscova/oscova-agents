using System.Security.Claims;
using NLog;

namespace Oscova.Agents.Host.Logging;

public sealed class UserScopeLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out _))
        {
            using (ScopeContext.PushProperty("userId", userId))
            {
                await next(context).ConfigureAwait(false);
            }

            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
