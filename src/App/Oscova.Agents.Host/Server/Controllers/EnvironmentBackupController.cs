using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Environment;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Route("api/environment")]
[IgnoreAntiforgeryToken]
public sealed class EnvironmentBackupController(
    EnvironmentBackupService backupService,
    UserManager<ApplicationUser> userManager,
    HostProcessRelauncher relauncher,
    OriginalHostArgs originalHostArgs,
    WebHostRuntimeState runtimeState,
    IHostApplicationLifetime applicationLifetime,
    RuntimeVersionService runtimeVersionService,
    ILogger<EnvironmentBackupController> logger) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpGet("reset")]
    public ActionResult<EnvironmentResetPreview> ResetPreview()
    {
        try
        {
            EnsureResetAvailable();
            backupService.ValidateResetDestination(GetProtectedPaths());
            return Ok(new EnvironmentResetPreview(Path.GetFullPath(PathUtil.AppDataPath)));
        }
        catch (EnvironmentBackupException ex)
        {
            return Conflict(new ProblemDetails { Title = "Environment reset unavailable", Detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Title = "Environment reset unavailable", Detail = ex.Message });
        }
    }

    /// <summary>Streams a backup for a cookie-authenticated browser with request verification.</summary>
    [Authorize(Policy = AgentsServerAuthenticationDefaults.BrowserBackupExportPolicy)]
    [ValidateAntiForgeryToken]
    [HttpPost("export/browser")]
    public Task<IActionResult> ExportFromBrowser(CancellationToken ct) => Export(ct);

    [Authorize(Roles = "Admin")]
    [HttpPost("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var passphrase = Request.HasFormContentType
            ? (await Request.ReadFormAsync(ct))["passphrase"].ToString()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return BadRequest("A passphrase is required.");
        }

        var userId = userManager.GetUserId(User) ?? string.Empty;
        var fileName = $"oscova-environment-{DateTime.UtcNow:yyyyMMdd-HHmmss}.oscb";
        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        await using var responseStream = Response.BodyWriter.AsStream(leaveOpen: true);
        var appVersion = runtimeVersionService.GetSnapshot().Host.FullVersion;
        await backupService.ExportAsync(responseStream, passphrase, userId, appVersion, ct);
        return new EmptyResult();
    }

    [AllowAnonymous]
    [HttpPost("restore")]
    [RequestSizeLimit(EnvironmentExportLimits.MaxUploadSizeBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = EnvironmentExportLimits.MaxUploadSizeBytes)]
    public async Task<IActionResult> Restore([FromForm] IFormFile? file, [FromForm] string? passphrase, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("A backup file is required.");
        }

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return BadRequest("A passphrase is required.");
        }

        if (!await backupService.TryAcquireAdmissionAsync(ct))
        {
            return Conflict("Another restore, Avatar operation or harness update is already pending.");
        }

        try
        {
            if (EnvironmentRestoreBootstrapper.HasPendingOperation())
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: ApiRoutes.Environment.RestorePendingProblemType,
                    title: "Environment restore pending",
                    detail: "An environment restore is already pending. Wait for Oscova to restart before trying again.");
            }

            // Startup's system credential row must not block restoring a fresh installation.
            if (await userManager.Users.AnyAsync(user => user.Id != WellKnownUsers.SystemId, ct))
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: ApiRoutes.Environment.RestoreFreshInstallRequiredProblemType,
                    title: "Fresh installation required",
                    detail: "Restore is only available on a fresh install. This host already has an account.");
            }

            var restoreStaged = false;
            try
            {
                await using var stream = file.OpenReadStream();
                await backupService.StageRestoreAsync(stream, passphrase, ct);
                restoreStaged = true;

                using var replacement = relauncher.StartReplacement();
                applicationLifetime.StopApplication();
                return Accepted();
            }
            catch (EnvironmentBackupException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                if (restoreStaged)
                {
                    try
                    {
                        EnvironmentBackupService.ClearPendingRestore();
                    }
                    catch (Exception cleanupEx)
                    {
                        logger.LogWarning(cleanupEx, "Could not clear pending environment restore after restart handoff failed.");
                    }

                    logger.LogError(ex, "Environment restore was staged from uploaded backup '{FileName}', but Oscova could not start a replacement process.", file.FileName);
                    return BadRequest("Oscova could not restart automatically. The backup was not applied.");
                }

                logger.LogError(ex, "Environment restore failed while staging uploaded backup '{FileName}'.", file.FileName);
                return BadRequest("The backup could not be restored.");
            }
        }
        finally
        {
            backupService.ReleaseAdmission();
        }
    }

    [Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.BlazorBearerScheme, Roles = "Admin")]
    [HttpPost("reset")]
    public async Task<ActionResult<EnvironmentResetAccepted>> Reset(
        ResetEnvironmentRequest request,
        CancellationToken ct)
    {
        if (!string.Equals(request.Confirmation, "RESET", StringComparison.Ordinal))
        {
            return BadRequest("Type RESET to confirm the environment reset.");
        }

        if (!await backupService.TryAcquireAdmissionAsync(ct))
        {
            return Conflict("Another restore, Avatar operation or harness update is already pending.");
        }

        try
        {
            EnsureResetAvailable();
            var activeBinding = runtimeState.ActiveBinding
                ?? throw new InvalidOperationException("The active Host binding is unavailable.");
            var operation = backupService.StageReset(GetProtectedPaths(), originalHostArgs.Args);
            var backupPath = EnvironmentRestoreBootstrapper.GetResetBackupPath(operation);

            try
            {
                using var replacement = relauncher.StartReplacement(activeBinding);
            }
            catch (Exception ex)
            {
                try
                {
                    EnvironmentBackupService.ClearPendingRestore();
                }
                catch (Exception cleanupEx)
                {
                    logger.LogWarning(cleanupEx, "Could not clear the prepared environment reset after restart handoff failed.");
                }

                logger.LogError(ex, "Environment reset was prepared, but Oscova could not start a replacement process.");
                return BadRequest("Oscova could not restart automatically. The environment was not reset.");
            }

            Response.OnCompleted(() =>
            {
                applicationLifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Accepted(new EnvironmentResetAccepted(operation.ResetOperationId!, backupPath));
        }
        catch (EnvironmentBackupException ex)
        {
            return Conflict(new ProblemDetails { Title = "Environment reset unavailable", Detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails { Title = "Environment reset unavailable", Detail = ex.Message });
        }
        finally
        {
            backupService.ReleaseAdmission();
        }
    }

    [AllowAnonymous]
    [HttpGet("reset/completion/{operationId}")]
    public ActionResult<EnvironmentResetCompletion> ResetCompletion(string operationId)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new EnvironmentResetCompletion(EnvironmentRestoreBootstrapper.IsResetCompleted(operationId)));
    }

    private static IReadOnlyList<string> GetProtectedPaths()
    {
        var cliRoot = Path.GetDirectoryName(RuntimeDescriptorWriter.RuntimePath)
            ?? throw new InvalidOperationException("CLI storage could not be resolved.");
        var paths = new List<string>
        {
            Path.Combine(cliRoot, "auth.json"),
            Path.Combine(cliRoot, "cli-config.yaml"),
            Path.Combine(cliRoot, "history"),
            Path.Combine(cliRoot, "runtime.json"),
            Path.Combine(cliRoot, "avatar"),
            Path.Combine(cliRoot, "updates"),
            ApplicationUpdateFiles.UpdatesDirectory,
            AppContext.BaseDirectory
        };
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            paths.Add(Environment.ProcessPath);
        }

        return paths.Select(Path.GetFullPath).ToArray();
    }

    private static void EnsureResetAvailable()
    {
        if (EnvironmentRestoreBootstrapper.HasPendingOperation() ||
            EnvironmentRestoreBootstrapper.HasRecoveryEvidence())
        {
            throw new EnvironmentBackupException(
                "Another environment operation or recovery is pending. Resolve it before resetting the environment.");
        }
    }
}
