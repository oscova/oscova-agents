using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Preserves authorization and interruption failures for governed settings requests.</summary>
public sealed class GovernedConfigurationExceptionAttribute : ExceptionFilterAttribute
{
    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            ExecutionPermissionRegistry.ConfirmationRequiredException or SkillImportConflictException => StatusCodes.Status409Conflict,
            UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            ArgumentException or InvalidOperationException => StatusCodes.Status400BadRequest,
            _ => 0
        };
        if (status == 0) { return; }

        context.Result = new ObjectResult(new ErrorResult { Message = context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
