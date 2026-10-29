using Oscova.Agents.Core.Auth;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Secrets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Admin;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Tasks;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.BlazorBearerScheme, Roles = "Admin")]
[Route("api/admin")]
[IgnoreAntiforgeryToken]
public sealed class AdminController(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext identityDb,
    SignInThrottle throttle,
    DuressRecordProtector duressRecords,
    IPasswordHasher<ApplicationUser> passwordHasher,
    AuthSettings authSettings,
    UserServiceManager userServiceManager,
    ThreadRunGuard threadRunGuard,
    ChatRunProjectionStore projectionStore,
    IVaultDataProtector vaultDataProtector,
    IUserDirectoryService userDirectoryService,
    IAgentTaskCatalog taskCatalog,
    AgentTaskSchedulerService taskScheduler,
    ILogger<AdminController> logger)
    : ControllerBase
{
    private static readonly string[] AvailableRoles = ["Admin", "Moderator", "User"];

    [HttpGet]
    public async Task<ActionResult<AdminSnapshot>> Get(CancellationToken ct)
    {
        var currentUserId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        return Ok(new AdminSnapshot
        {
            AuthMode = authSettings.Mode,
            RequirePinForHome = authSettings.RequirePinForHome,
            HasAdminPin = await HasAnyAdminPinAsync(),
            AvailableRoles = [.. AvailableRoles],
            Users = await LoadUsersAsync(currentUserId, ct)
        });
    }

    [HttpPut("auth-settings")]
    public async Task<IActionResult> SaveAuthSettings([FromBody] SaveAuthSettingsRequest request)
    {
        var currentUser = await userManager.GetUserAsync(User);
        if (currentUser is null)
        {
            return Unauthorized();
        }

        if (request.Mode == AuthMode.Home)
        {
            if (string.IsNullOrWhiteSpace(currentUser.PinHash))
            {
                return BadRequest(new ErrorResult { Message = "Set a PIN on your operator account before enabling Home mode." });
            }

            if (!await HasAnyAdminPinAsync())
            {
                return BadRequest(new ErrorResult { Message = "At least one operator PIN is required before enabling Home mode." });
            }
        }

        authSettings.Mode = request.Mode;
        authSettings.RequirePinForHome = true;
        await authSettings.SaveAsync();
        return NoContent();
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, [FromServices] IUserPermissionService permissions)
    {
        if (request.Password != request.ConfirmPassword)
        {
            return BadRequest(new ErrorResult { Message = "Passwords do not match." });
        }

        var existing = await userManager.FindByNameAsync(request.Username);
        if (existing is not null)
        {
            return BadRequest(new ErrorResult { Message = "A user with this username already exists." });
        }

        var selectedRoles = request.SelectedRoles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rolesToAssign = selectedRoles.Count > 0
            ? selectedRoles
            : new HashSet<string>(["User"], StringComparer.OrdinalIgnoreCase);
        var unknownRoles = GetUnknownRoles(rolesToAssign);
        if (unknownRoles.Length > 0)
        {
            return BadRequest(ToUnknownRolesError(unknownRoles));
        }

        // Account administration cannot create a second installation operator.
        if (rolesToAssign.Contains("Admin", StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResult { Message = "This installation already has an operator." });
        }

        var user = new ApplicationUser
        {
            UserName = request.Username,
            Email = request.Email,
            FullName = request.FullName
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            return BadRequest(ToError(result));
        }

        var addRoles = await userManager.AddToRolesAsync(user, rolesToAssign);
        if (!addRoles.Succeeded)
        {
            var deleteUser = await userManager.DeleteAsync(user);
            if (!deleteUser.Succeeded)
            {
                logger.LogError(
                    "User {UserId} was created but role assignment and cleanup both failed. Role errors: {RoleErrors} Cleanup errors: {CleanupErrors}",
                    user.Id,
                    FormatIdentityErrors(addRoles),
                    FormatIdentityErrors(deleteUser));
            }

            return BadRequest(ToError(addRoles));
        }

        // Projects, both shells and the shared folder are basic grants: project access is not useful without running commands.
        // Skill, MCP and personal model grants stay off until the operator enables them.
        await permissions.SaveAsync(user.Id, [UserPermissionKeys.Projects, UserPermissionKeys.WorkspaceShell, UserPermissionKeys.ProjectShell,
            UserPermissionKeys.SharedFolder]);

        // Eagerly seed per-user runtime files so background tasks and Discord can address this user immediately.
        await userServiceManager.ProvisionUserAsync(user.Id);

        return Ok(new SuccessResult { Message = $"User '{user.UserName}' created." });
    }

    /// <summary>Returns the selected account's current execution permissions.</summary>
    [HttpGet("users/{userId}/permissions")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetUserPermissions(
        string userId, [FromServices] IUserPermissionService permissions, CancellationToken ct)
    {
        if (await userManager.FindByIdAsync(userId) is null) { return NotFound(); }

        return Ok(await permissions.GetGrantedAsync(userId, ct).ConfigureAwait(false));
    }

    /// <summary>Replaces the selected regular account's operator-controlled permission grants.</summary>
    [HttpPut("users/{userId}/permissions")]
    public async Task<IActionResult> SaveUserPermissions(
        string userId, [FromBody] string[] granted,
        [FromServices] IUserPermissionService permissions, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) { return NotFound(); }

        if (await userManager.IsInRoleAsync(user, "Admin"))
        {
            return BadRequest(new ErrorResult { Message = "The operator already has all execution permissions." });
        }

        try
        {
            await permissions.SaveAsync(userId, granted, ct).ConfigureAwait(false);
            return Ok(new SuccessResult { Message = "Execution permissions saved." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResult { Message = ex.Message });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("users/{userId}/profile")]
    public async Task<IActionResult> SaveUserProfile(string userId, [FromBody] SaveUserProfileRequest request)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var newUserName = (request.UserName ?? string.Empty).Trim();
        var newEmail = (request.Email ?? string.Empty).Trim();
        var newFullName = (request.FullName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(newUserName) || newUserName.Length < 3)
        {
            return BadRequest(new ErrorResult { Message = "Username must be at least 3 characters." });
        }

        if (string.IsNullOrWhiteSpace(newEmail))
        {
            return BadRequest(new ErrorResult { Message = "Email is required." });
        }

        // A rejected profile save must leave every Identity field unchanged.
        await using var transaction = await identityDb.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        var changed = false;

        if (!string.Equals(user.UserName ?? string.Empty, newUserName, StringComparison.Ordinal))
        {
            var existingByName = await userManager.FindByNameAsync(newUserName);
            if (existingByName is not null && existingByName.Id != user.Id)
            {
                return BadRequest(new ErrorResult { Message = "A user with this username already exists." });
            }

            var setName = await userManager.SetUserNameAsync(user, newUserName);
            if (!setName.Succeeded)
            {
                return BadRequest(ToError(setName));
            }

            changed = true;
        }

        if (!string.Equals(user.Email ?? string.Empty, newEmail, StringComparison.OrdinalIgnoreCase))
        {
            var existingByEmail = await userManager.FindByEmailAsync(newEmail);
            if (existingByEmail is not null && existingByEmail.Id != user.Id)
            {
                return BadRequest(new ErrorResult { Message = "A user with this email already exists." });
            }

            var setEmail = await userManager.SetEmailAsync(user, newEmail);
            if (!setEmail.Succeeded)
            {
                return BadRequest(ToError(setEmail));
            }

            changed = true;
        }

        if (!string.Equals(user.FullName ?? string.Empty, newFullName, StringComparison.Ordinal))
        {
            user.FullName = newFullName;
            changed = true;
        }

        if (!changed)
        {
            return Ok(new SuccessResult { Message = "No profile changes." });
        }

        var update = await userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            return BadRequest(ToError(update));
        }

        await transaction.CommitAsync(HttpContext.RequestAborted);
        // Retained instruction variables follow the committed identity, just as self-service edits do.
        userServiceManager.RefreshProfile(user.Id, user.FullName ?? string.Empty, user.UserName ?? string.Empty, user.Email ?? string.Empty);
        return Ok(new SuccessResult { Message = $"Updated profile for '{user.UserName}'." });
    }

    [HttpPut("users/{userId}/roles")]
    public async Task<IActionResult> SaveUserRoles(string userId, [FromBody] SaveUserRolesRequest request)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var currentRoles = await userManager.GetRolesAsync(user);
        var selectedRoles = request.SelectedRoles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownRoles = GetUnknownRoles(selectedRoles);
        if (unknownRoles.Length > 0)
        {
            return BadRequest(ToUnknownRolesError(unknownRoles));
        }

        var toRemove = currentRoles.Except(selectedRoles, StringComparer.OrdinalIgnoreCase).ToArray();
        var toAdd = selectedRoles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToArray();

        // Existing operator role saves are allowed, but regular accounts cannot become operators.
        if (toAdd.Contains("Admin", StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResult { Message = "This installation already has an operator." });
        }

        if (toRemove.Contains("Admin", StringComparer.OrdinalIgnoreCase))
        {
            if (await WouldRemoveLastPinnedAdminInHomeModeAsync(user))
            {
                return BadRequest(new ErrorResult
                {
                    Message = "This admin role cannot be removed while Home mode is active and no other admin PIN would remain."
                });
            }

            var admins = await userManager.GetUsersInRoleAsync("Admin");
            var onlyAdmin = admins.SingleOrDefault();
            if (admins.Count <= 1 && onlyAdmin?.Id == user.Id)
            {
                return BadRequest(new ErrorResult { Message = "At least one admin must remain." });
            }
        }

        if (toAdd.Length > 0)
        {
            var addRoles = await userManager.AddToRolesAsync(user, toAdd);
            if (!addRoles.Succeeded)
            {
                var rollbackAdd = await RemoveRolesIfPresentAsync(user, toAdd);
                if (!rollbackAdd.Succeeded)
                {
                    logger.LogError(
                        "Role update for user {UserId} failed and added-role rollback also failed. Add errors: {AddErrors} Rollback errors: {RollbackErrors}",
                        user.Id,
                        FormatIdentityErrors(addRoles),
                        FormatIdentityErrors(rollbackAdd));
                }

                return BadRequest(ToError(addRoles));
            }
        }

        if (toRemove.Length > 0)
        {
            var removeRoles = await userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removeRoles.Succeeded)
            {
                if (toAdd.Length > 0)
                {
                    var rollbackAdd = await RemoveRolesIfPresentAsync(user, toAdd);
                    if (!rollbackAdd.Succeeded)
                    {
                        logger.LogError(
                            "Role update for user {UserId} failed and added-role rollback also failed. Remove errors: {RemoveErrors} Rollback errors: {RollbackErrors}",
                            user.Id,
                            FormatIdentityErrors(removeRoles),
                            FormatIdentityErrors(rollbackAdd));
                    }
                }

                var restoreRoles = await AddRolesIfMissingAsync(user, toRemove);
                if (!restoreRoles.Succeeded)
                {
                    logger.LogError(
                        "Role update for user {UserId} failed and removed-role restore also failed. Remove errors: {RemoveErrors} Restore errors: {RestoreErrors}",
                        user.Id,
                        FormatIdentityErrors(removeRoles),
                        FormatIdentityErrors(restoreRoles));
                }

                return BadRequest(ToError(removeRoles));
            }
        }

        // Publish the successful Identity update into any retained runtime scope used by later requests.
        userServiceManager.RefreshRoles(
            user.Id,
            selectedRoles.ToHashSet(StringComparer.OrdinalIgnoreCase));

        return Ok(new SuccessResult { Message = $"Updated roles for '{user.UserName}'." });
    }

    [HttpPut("users/{userId}/password-reset")]
    public async Task<IActionResult> ResetPassword(string userId, [FromBody] ResetPasswordRequest request)
    {
        var currentUserId = userManager.GetUserId(User);
        if (string.Equals(currentUserId, userId, StringComparison.Ordinal))
        {
            return BadRequest(new ErrorResult { Message = "Use the profile dialog to change your password." });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        {
            return BadRequest(new ErrorResult { Message = "Please provide a password with at least 6 characters." });
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!resetResult.Succeeded)
        {
            return BadRequest(ToError(resetResult));
        }

        return Ok(new SuccessResult { Message = $"Password reset for '{user.UserName}'." });
    }

    [HttpPut("users/{userId}/pin")]
    public async Task<IActionResult> SaveUserPin(string userId, [FromBody] SaveUserPinRequest request)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) { return NotFound(); }

        var gate = throttle.GetAccountGate(user.Id);
        await gate.WaitAsync();
        try
        {
            if (!PinPolicy.IsValid(request.NewPin))
            {
                return BadRequest(new ErrorResult { Message = "PIN must be 6–64 digits." });
            }

            if (!string.Equals(request.NewPin, request.ConfirmPin, StringComparison.Ordinal))
            {
                return BadRequest(new ErrorResult { Message = "PIN confirmation does not match." });
            }

            await identityDb.Entry(user).ReloadAsync();
            if (identityDb.Entry(user).State == EntityState.Detached) { return NotFound(); }

            if (duressRecords.OpenPinSlot(user.Id, user.DuressRecord, request.NewPin).Matched)
            {
                return BadRequest(new ErrorResult { Message = "This PIN cannot be used. Choose a different PIN." });
            }

            user.PinHash = passwordHasher.HashPassword(user, request.NewPin);
            var result = await userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                return BadRequest(ToError(result));
            }

            return Ok(new SuccessResult { Message = $"PIN updated for '{user.UserName}'." });
        }
        finally
        {
            gate.Release();
        }
    }

    [HttpDelete("users/{userId}/pin")]
    public async Task<IActionResult> ClearUserPin(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) { return NotFound(); }

        var gate = throttle.GetAccountGate(user.Id);
        await gate.WaitAsync();
        try
        {
            await identityDb.Entry(user).ReloadAsync();
            if (identityDb.Entry(user).State == EntityState.Detached) { return NotFound(); }

            if (await WouldRemoveLastPinnedAdminInHomeModeAsync(user))
            {
                return BadRequest(new ErrorResult
                {
                    Message = "This PIN cannot be cleared while Home mode is active and no other admin PIN would remain."
                });
            }

            user.PinHash = null;
            var result = await userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                return BadRequest(ToError(result));
            }

            return Ok(new SuccessResult { Message = $"PIN cleared for '{user.UserName}'." });
        }
        finally
        {
            gate.Release();
        }
    }

    [HttpDelete("users/{userId}")]
    public async Task<IActionResult> DeleteUser(string userId)
    {
        var currentUserId = userManager.GetUserId(User);
        if (string.Equals(currentUserId, userId, StringComparison.Ordinal))
        {
            return BadRequest(new ErrorResult { Message = "You cannot delete your own account." });
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var roles = await userManager.GetRolesAsync(user);
        if (roles.Contains("Admin", StringComparer.OrdinalIgnoreCase))
        {
            if (await WouldRemoveLastPinnedAdminInHomeModeAsync(user))
            {
                return BadRequest(new ErrorResult
                {
                    Message = "This admin cannot be deleted while Home mode is active and no other admin PIN would remain."
                });
            }

            var admins = await userManager.GetUsersInRoleAsync("Admin");
            if (admins.Count <= 1)
            {
                return BadRequest(new ErrorResult { Message = "At least one admin must remain." });
            }
        }

        using var deletionLease = await threadRunGuard.CancelAndAcquireUserAsync(
            userId,
            HttpContext.RequestAborted);
        // Scheduled executions own a separate lifetime and must finish before their user files are removed.
        using var taskDeletionLease = await taskScheduler.CancelAndAcquireUserAsync(userId, HttpContext.RequestAborted);
        IdentityResult result;
        try
        {
            result = await userManager.DeleteAsync(user);
        }
        // A busy BEGIN or COMMIT of the save's transaction surfaces unwrapped; a busy statement arrives as DbUpdateException.
        catch (Exception ex) when (ex is SqliteException { SqliteErrorCode: 5 or 6 }
            or DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 } })
        {
            logger.LogError(ex, "Database contention prevented deletion of user {UserId}.", userId);
            return Conflict(new ErrorResult
            {
                Message = $"User '{user.UserName}' could not be deleted because the database is busy. Wait for other activity to finish, then try again."
            });
        }

        if (!result.Succeeded)
        {
            return BadRequest(ToError(result));
        }

        projectionStore.RemoveUser(userId);
        try
        {
            // Account deletion never opens the target vault or creates its runtime services.
            if (userServiceManager.TryGetExistingScope(userId, out var userScope))
            {
                await userScope.LibraryIndexingService.DisposeAsync();
            }

            vaultDataProtector.Forget(userId);
            await userServiceManager.RemoveScopeAsync(userId);
            var filesRemoved = await userDirectoryService.DeleteUserRootAsync(userId, HttpContext.RequestAborted);
            await taskCatalog.ReloadAsync(HttpContext.RequestAborted);
            logger.LogInformation(
                "Reaped artifacts for deleted user {UserId}: {Files} files removed.",
                userId,
                filesRemoved);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Identity record for {UserId} was deleted but artifact cleanup failed.", userId);
            return Ok(new SuccessResult
            {
                HasWarnings = true,
                Message = $"Deleted user '{user.UserName}' but some artifacts could not be removed. See server logs for the cause and remaining files."
            });
        }
        finally
        {
            vaultDataProtector.Forget(userId);
            try
            {
                await userServiceManager.RemoveScopeAsync(userId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispose the deleted user's cached scope for {UserId}.", userId);
            }
        }

        return Ok(new SuccessResult { Message = $"Deleted user '{user.UserName}'." });
    }

    private async Task<List<UserEntry>> LoadUsersAsync(string currentUserId, CancellationToken ct)
    {
        var users = await userManager.Users
            .Where(user => user.Id != WellKnownUsers.SystemId)
            .OrderBy(user => user.UserName)
            .ToListAsync(ct);
        var entries = new List<UserEntry>(users.Count);

        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            entries.Add(new UserEntry
            {
                Id = user.Id,
                UserName = user.UserName ?? string.Empty,
                FullName = user.FullName ?? string.Empty,
                Email = user.Email ?? string.Empty,
                SelectedRoles = [.. roles.OrderBy(role => role, StringComparer.OrdinalIgnoreCase)],
                HasPin = !string.IsNullOrWhiteSpace(user.PinHash),
                IsCurrentUser = string.Equals(currentUserId, user.Id, StringComparison.Ordinal)
            });
        }

        return entries;
    }

    private async Task<bool> HasAnyAdminPinAsync()
    {
        var admins = await userManager.GetUsersInRoleAsync("Admin");
        return admins.Any(admin => !string.IsNullOrWhiteSpace(admin.PinHash));
    }

    private async Task<bool> WouldRemoveLastPinnedAdminInHomeModeAsync(ApplicationUser user)
    {
        if (authSettings.Mode != AuthMode.Home)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(user.PinHash))
        {
            return false;
        }

        if (!await userManager.IsInRoleAsync(user, "Admin"))
        {
            return false;
        }

        var admins = await userManager.GetUsersInRoleAsync("Admin");
        var remainingPinnedAdmins = admins.Count(admin =>
            admin.Id != user.Id &&
            !string.IsNullOrWhiteSpace(admin.PinHash));

        return remainingPinnedAdmins == 0;
    }

    private async Task<IdentityResult> AddRolesIfMissingAsync(ApplicationUser user, IReadOnlyCollection<string> roles)
    {
        var currentRoles = await userManager.GetRolesAsync(user);
        var missingRoles = roles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToArray();

        return missingRoles.Length == 0
            ? IdentityResult.Success
            : await userManager.AddToRolesAsync(user, missingRoles);
    }

    private async Task<IdentityResult> RemoveRolesIfPresentAsync(ApplicationUser user, IReadOnlyCollection<string> roles)
    {
        var currentRoles = await userManager.GetRolesAsync(user);
        var presentRoles = roles.Intersect(currentRoles, StringComparer.OrdinalIgnoreCase).ToArray();

        return presentRoles.Length == 0
            ? IdentityResult.Success
            : await userManager.RemoveFromRolesAsync(user, presentRoles);
    }

    private static string[] GetUnknownRoles(IEnumerable<string> roles)
    {
        return roles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(role => !AvailableRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ErrorResult ToUnknownRolesError(IReadOnlyList<string> roles)
    {
        return new ErrorResult
        {
            Message = $"Unknown roles: {string.Join(", ", roles.Select(FormatRoleName))}."
        };
    }

    private static string FormatRoleName(string role)
    {
        return string.IsNullOrWhiteSpace(role) ? "<blank>" : role;
    }

    private static ErrorResult ToError(IdentityResult result)
    {
        return new ErrorResult
        {
            Message = FormatIdentityErrors(result)
        };
    }

    private static string FormatIdentityErrors(IdentityResult result)
    {
        return string.Join(" ", result.Errors.Select(error => error.Description));
    }
}
