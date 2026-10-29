using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Profile;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Tasks;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Tasks;
using Oscova.Agents.Host.Server.Nodes;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/profile")]
[IgnoreAntiforgeryToken]
public sealed class ProfileController(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext identityDb,
    SignInThrottle throttle,
    TimeProvider clock,
    IPasswordHasher<ApplicationUser> passwordHasher,
    UserServiceManager userServiceManager,
    OperatorVaultAccess operatorVault,
    IVaultDataProtector vaultDataProtector,
    DuressRecordProtector duressRecords,
    PendingDuressSelections pendingDuress,
    Oscova.Agents.Host.Server.Services.DuressActionExecutor duressActions,
    Oscova.Agents.Host.Server.Services.ChatSessionService chatSessionService,
    IAgentTaskCatalog agentTaskCatalog,
    IUserDirectoryService userDirectoryService,
    NodePluginDeploymentService nodePlugins,
    RecordFormatMigration recordFormatMigration,
    ILogger<ProfileController> logger) : ControllerBase
{
#if DEBUG
    /// <summary>Unlocks the configured development account through the standard vault workflow.</summary>
    [HttpGet("debug-unlock")]
    public async Task<IActionResult> DebugUnlock([FromServices] IConfiguration configuration, string? returnUrl = "/")
    {
        if (!Security.DebugAutoLoginMiddleware.IsAllowed(HttpContext) ||
            !string.Equals(User.Identity?.Name, configuration["DebugAutoLogin:Username"], StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var result = await UnlockVault(new UnlockVaultRequest { Password = configuration["DebugAutoLogin:VaultPassword"]! }, HttpContext.RequestAborted);
        if (result.Result is not OkObjectResult) { return result.Result!; }

        // Seeded settings are vault-encrypted, so seeding waits for the unlock.
        var user = await userManager.GetUserAsync(User);
        await Security.DebugOperatorSeeder.SeedAsync(HttpContext, await userServiceManager.GetOrCreateScopeAsync(user!.Id));
        return LocalRedirect(Utils.LoginNavigation.LocalReturnUrl(returnUrl));
    }
#endif

    [HttpGet]
    public async Task<ActionResult<UserProfile>> Get()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var userScope = await userServiceManager.GetOrCreateScopeAsync(user.Id);
        return Ok(new UserProfile
        {
            UserName = user.UserName ?? string.Empty,
            FullName = user.FullName ?? string.Empty,
            Nickname = userScope.ProfileSettings.Nickname,
            Email = user.Email ?? string.Empty
        });
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SaveProfileRequest request)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        // Identity setters persist independently, so rejection must roll back the whole profile edit.
        await using var transaction = await identityDb.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        var currentEmail = user.Email ?? string.Empty;
        var newEmail = request.Email ?? string.Empty;
        if (!string.Equals(currentEmail, newEmail, StringComparison.OrdinalIgnoreCase))
        {
            var existingByEmail = await userManager.FindByEmailAsync(newEmail);
            if (existingByEmail is not null && existingByEmail.Id != user.Id)
            {
                return BadRequest(new ErrorResult { Message = "This email address is already in use." });
            }

            var setEmailResult = await userManager.SetEmailAsync(user, newEmail);
            if (!setEmailResult.Succeeded)
            {
                return BadRequest(ToError(setEmailResult));
            }
        }

        if (!string.Equals(user.FullName ?? string.Empty, request.FullName ?? string.Empty, StringComparison.Ordinal))
        {
            user.FullName = request.FullName ?? string.Empty;
            var update = await userManager.UpdateAsync(user);
            if (!update.Succeeded)
            {
                return BadRequest(ToError(update));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.NewPassword))
        {
            if (!string.Equals(request.NewPassword, request.ConfirmNewPassword, StringComparison.Ordinal))
            {
                return BadRequest(new ErrorResult { Message = "The new passwords do not match." });
            }

            var changeResult = await userManager.ChangePasswordAsync(
                user,
                request.CurrentPassword ?? string.Empty,
                request.NewPassword);

            if (!changeResult.Succeeded)
            {
                return BadRequest(ToError(changeResult));
            }
        }

        await transaction.CommitAsync(HttpContext.RequestAborted);
        var userScope = await userServiceManager.GetOrCreateScopeAsync(user.Id);
        // Profile personalization shares this endpoint so the dialog keeps one save action.
        userScope.ProfileSettings.Nickname = request.Nickname?.Trim() ?? string.Empty;
        await userScope.ProfileSettings.SaveAsync();

        userServiceManager.RefreshProfile(
            user.Id,
            user.FullName ?? string.Empty,
            user.UserName ?? string.Empty,
            user.Email ?? string.Empty);

        return Ok(new SuccessResult { Message = "Your profile has been updated." });
    }

    /// <summary>Gets the current user's vault state.</summary>
    [HttpGet("vault")]
    public async Task<ActionResult<VaultStatus>> GetVaultStatus(CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var retryUntil = await throttle.GetRetryUntilAsync(user.Id, CredentialCategory.Vault, ct);
        var status = await ReadVaultStatusAsync(user.Id, ct, retryUntil);
        return Ok(status);
    }

    /// <summary>Creates the current user's Vault Password.</summary>
    [HttpPost("vault/setup")]
    public async Task<ActionResult<VaultStatus>> SetVaultPassword(
        [FromBody] SetVaultPasswordRequest request,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var validationError = ValidateVaultPassword(request.Password, request.ConfirmPassword);
        if (validationError is not null)
        {
            return BadRequest(new ErrorResult { Message = validationError });
        }

        var state = await vaultDataProtector.GetStateAsync(user.Id, ct);
        if (state.IsConfigured)
        {
            return Conflict(new ErrorResult { Message = "A Vault Password is already configured." });
        }

        await vaultDataProtector.SetPasswordAsync(user.Id, request.Password, ct);
        await userServiceManager.ProvisionUserAsync(user.Id, ct);
        await recordFormatMigration.RunAsync(await userServiceManager.GetOrCreateScopeAsync(user.Id, ct), ct);
        await chatSessionService.CleanupAgentReferencesAfterUnlockAsync(user.Id, ct);
        return Ok(await ReadVaultStatusAsync(user.Id, ct));
    }

    /// <summary>Unlocks the current user's vault for this Host process.</summary>
    [HttpPost("vault/unlock")]
    public async Task<ActionResult<VaultStatus>> UnlockVault(
        [FromBody] UnlockVaultRequest request,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var gate = throttle.GetAccountGate(user.Id);
        await gate.WaitAsync(ct);
        try
        {
            var state = await vaultDataProtector.GetStateAsync(user.Id, ct);
            if (!state.IsConfigured)
            {
                return Conflict(new ErrorResult { Message = "A Vault Password is not configured." });
            }

            if (string.IsNullOrEmpty(request.Password))
            {
                return BadRequest(new ErrorResult { Message = "The Vault Password is incorrect." });
            }

            // VaultDataProtector persists admission before it makes the decrypted key available.
            var result = await vaultDataProtector.UnlockAsync(user.Id, request.Password, ct);
            if (result.RetryUntil is { } retryUntil) { return VaultRetry(retryUntil); }

            if (!result.IsAccepted)
            {
                return BadRequest(new ErrorResult { Message = "The Vault Password is incorrect." });
            }

            if (pendingDuress.TryGet(user.Id, out var actions))
            {
                var succeeded = await duressActions.ExecuteAsync(user, actions);
                if (!succeeded)
                {
                    vaultDataProtector.Forget(user.Id);
                    pendingDuress.Remove(user.Id);
                    return BadRequest(new ErrorResult { Message = "The vault could not be unlocked." });
                }

                pendingDuress.Remove(user.Id);
            }

            await userServiceManager.ProvisionUserAsync(user.Id, ct);
            var userScope = await userServiceManager.GetOrCreateScopeAsync(user.Id, ct);

            // Converting old records needs the decrypted catalogs, and must finish before bots and tasks write new ones.
            await recordFormatMigration.RunAsync(userScope, ct);
            await chatSessionService.CleanupAgentReferencesAfterUnlockAsync(user.Id, ct);
            await userScope.AppService.RefreshThreadSummariesAsync();
            await userScope.LibraryIndexingService.QueueReconciliationAsync(CancellationToken.None);

            // Resume preparation for devices that connected while this account's vault was locked.
            nodePlugins.ActivationChanged(user.Id);

            try
            {
                await userServiceManager.StartEnabledBotsAsync(user.Id, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The vault was unlocked, but enabled bots could not be retried for user {UserId}.", user.Id);
            }

            return Ok(await ReadVaultStatusAsync(user.Id, ct));

        }
        catch (Exception ex)
        {
            // Authentication alone must not expose a vault whose stored data could not be prepared.
            vaultDataProtector.Forget(user.Id);
            if (ex is not (InvalidDataException or FormatException)) { throw; }

            logger.LogError(ex, "Encrypted data could not be prepared while unlocking the vault for user {UserId}.", user.Id);
            return UnprocessableEntity(new ErrorResult
            {
                Message = "Your Vault Password was accepted, but encrypted data could not be opened. Restore a compatible backup or use an application version that supports this data."
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Changes the password without changing the user's vault key.</summary>
    [HttpPost("vault/change")]
    public async Task<ActionResult<VaultStatus>> ChangeVaultPassword(
        [FromBody] ChangeVaultPasswordRequest request,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var validationError = ValidateVaultPassword(request.NewPassword, request.ConfirmNewPassword);
        if (validationError is not null)
        {
            return BadRequest(new ErrorResult { Message = validationError });
        }

        var state = await vaultDataProtector.GetStateAsync(user.Id, ct);
        if (!state.IsConfigured)
        {
            return Conflict(new ErrorResult { Message = "A Vault Password is not configured." });
        }

        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            return BadRequest(new ErrorResult { Message = "The Vault Password is incorrect." });
        }

        var result = await vaultDataProtector.ChangePasswordAsync(
            user.Id, request.CurrentPassword, request.NewPassword, ct);
        if (result.RetryUntil is { } retryUntil) { return VaultRetry(retryUntil); }

        if (!result.IsAccepted)
        {
            return BadRequest(new ErrorResult { Message = "The Vault Password is incorrect." });
        }

        return Ok(await ReadVaultStatusAsync(user.Id, ct));
    }

    /// <summary>Replaces the current user's duress credentials and shared deletion selection.</summary>
    [HttpPost("duress-credentials")]
    public async Task<IActionResult> SaveDuressCredentials(
        [FromBody] SaveDuressCredentialsRequest request,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var gate = throttle.GetAccountGate(user.Id);
        await gate.WaitAsync(ct);
        try
        {
            await identityDb.Entry(user).ReloadAsync(ct);
            if (identityDb.Entry(user).State == EntityState.Detached) { return Unauthorized(); }

            if (await userManager.GetTwoFactorEnabledAsync(user))
            {
                return BadRequest(new ErrorResult
                {
                    Message = "Duress credentials cannot be set while two-factor authentication is enabled."
                });
            }

            if (request.DuressPassword is null || request.DuressPassword.Length is < 12 or > 256)
            {
                return BadRequest(new ErrorResult
                {
                    Message = "The duress password must be between 12 and 256 characters."
                });
            }

            if (!string.Equals(request.DuressPassword, request.ConfirmDuressPassword, StringComparison.Ordinal))
            {
                return BadRequest(new ErrorResult { Message = "The duress password confirmation does not match." });
            }

            if (string.Equals(request.DuressPassword, request.AccountPassword, StringComparison.Ordinal))
            {
                return BadRequest(new ErrorResult
                {
                    Message = "The duress password must be different from your account password."
                });
            }

            if (string.IsNullOrEmpty(request.AccountPassword) ||
                !await userManager.CheckPasswordAsync(user, request.AccountPassword))
            {
                return BadRequest(new ErrorResult { Message = "Your account password is incorrect." });
            }

            var pin = string.IsNullOrEmpty(request.DuressPin) ? null : request.DuressPin;
            var confirmation = string.IsNullOrEmpty(request.ConfirmDuressPin) ? null : request.ConfirmDuressPin;
            if (!string.Equals(pin, confirmation, StringComparison.Ordinal))
            {
                return BadRequest(new ErrorResult { Message = "The duress PIN confirmation does not match." });
            }

            if (pin is not null)
            {
                if (!PinPolicy.IsValid(pin)) { return BadRequest(new ErrorResult { Message = "PIN must be 6–64 digits." }); }

                if (string.IsNullOrWhiteSpace(user.PinHash)) { return BadRequest(new ErrorResult { Message = "Set a sign-in PIN before configuring a duress PIN." }); }

                if (passwordHasher.VerifyHashedPassword(user, user.PinHash, pin) != PasswordVerificationResult.Failed)
                {
                    return BadRequest(new ErrorResult { Message = "Choose a PIN different from your sign-in PIN." });
                }
            }

            var agentIds = request.AgentIds ?? [];
            var taskIds = request.TaskIds ?? [];
            var workspaces = request.Workspaces ?? [];
            if (request.ClearSharedFolder && !User.IsInRole("Admin"))
            {
                return BadRequest(new ErrorResult { Message = "Only the operator can select the shared folder." });
            }

            var actions = new DuressActions
            {
                DeleteAllThreads = request.DeleteAllThreads,
                AgentIds = agentIds,
                TaskIds = taskIds,
                Workspaces = workspaces,
                ClearSharedFolder = request.ClearSharedFolder
            };
            if (!DuressRecordProtector.ValidateActions(actions, out var validationError))
            {
                return BadRequest(new ErrorResult { Message = validationError! });
            }

            var userScope = await userServiceManager.GetOrCreateScopeAsync(user.Id, ct);
            foreach (var agentId in agentIds.Select(static id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var agent = userScope.AppService.CustomAgentDefinitions.FirstOrDefault(existing =>
                    string.Equals(existing.Id, agentId, StringComparison.Ordinal));
                if (agent?.IsShared == true &&
                    string.Equals(agent.OwnerUserId, user.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new ErrorResult { Message = "A shared agent cannot be selected." });
                }

                if (agent is not { IsShared: false } ||
                    !string.Equals(agent.OwnerUserId, user.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(UnavailableSelection());
                }
            }

            var availableTaskIds = (await userScope.AgentTaskRepository.GetTasksByUserAsync(user.Id, ct))
                .Where(task => !WellKnownTasks.ReservedFolderKeys.Contains(
                    Path.GetFileName(agentTaskCatalog.TryGetTaskDirectory(task.Id)),
                    StringComparer.OrdinalIgnoreCase))
                .Select(task => task.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (taskIds.Select(static id => id.Trim()).Any(id => !availableTaskIds.Contains(id)))
            {
                return BadRequest(UnavailableSelection());
            }

            // Seal the actual directory name so deletion also resolves it on case-sensitive filesystems.
            var availableWorkspaces = userDirectoryService.ListWorkspaces(user.Id).ToHashSet(StringComparer.Ordinal);
            if (actions.Workspaces.Select(static workspace => workspace.Trim()).Any(workspace => !availableWorkspaces.Contains(workspace)))
            {
                return BadRequest(UnavailableSelection());
            }

            var previousRecord = user.DuressRecord;
            user.DuressRecord = duressRecords.SetDuressCredentials(
                user.Id,
                request.AccountPassword,
                request.DuressPassword,
                actions, pin);
            var update = await userManager.UpdateAsync(user);
            if (!update.Succeeded)
            {
                user.DuressRecord = previousRecord;
                return BadRequest(ToError(update));
            }

            return Ok(new SuccessResult { Message = "Your duress credentials have been saved." });
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? ValidateVaultPassword(string? password, string? confirmation)
    {
        if (password is null || password.Length is < 12 or > 256)
        {
            return "The Vault Password must be between 12 and 256 characters.";
        }

        return string.Equals(password, confirmation, StringComparison.Ordinal)
            ? null
            : "The Vault Password confirmation does not match.";
    }

    /// <summary>Reports both vault gates without loading either account's encrypted settings.</summary>
    private async Task<VaultStatus> ReadVaultStatusAsync(string userId, CancellationToken ct, DateTimeOffset? retryUntil = null)
    {
        var state = await vaultDataProtector.GetStateAsync(userId, ct);
        return new VaultStatus
        {
            IsConfigured = state.IsConfigured,
            IsUnlocked = state.IsUnlocked && !pendingDuress.Contains(userId),
            IsOperatorReady = await operatorVault.IsReadyAsync(userId, ct),
            RetryUntil = retryUntil
        };
    }

    /// <summary>Returns the persisted deadline used by both vault password forms.</summary>
    private ObjectResult VaultRetry(DateTimeOffset retryUntil)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((retryUntil - clock.GetUtcNow()).TotalSeconds));
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new VaultRetryErrorResult
        {
            Message = "Wait before trying your Vault Password again.",
            RetryUntil = retryUntil
        });
    }

    private static ErrorResult ToError(IdentityResult result)
    {
        return new ErrorResult
        {
            Message = string.Join(" ", result.Errors.Select(error => error.Description))
        };
    }

    private static ErrorResult UnavailableSelection() => new()
    {
        Message = "One of the selected items is no longer available. Reopen the form and choose again."
    };
}
