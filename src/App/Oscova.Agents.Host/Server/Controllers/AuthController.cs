using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.ApiContracts.Auth;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Host.Utils;
#if DEBUG
using Oscova.Agents.Core;
using Oscova.Agents.Infrastructure.Backup;
#endif

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Route("auth")]
[IgnoreAntiforgeryToken] // Blazor submits server-owned tickets before browser cookie issuance.
public sealed class AuthController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    LoginTicketService ticketService,
    ChatSessionService chatSessionService,
    DuressRecordProtector duressRecords,
    DuressActionExecutor duressActions,
    Oscova.Agents.Core.Secrets.IVaultDataProtector vaultDataProtector,
    PendingDuressSelections pendingDuress,
    SignInThrottle throttle,
    ApplicationDbContext identityDb,
    IPasswordHasher<ApplicationUser> passwordHasher,
    AuthSettings authSettings,
    TimeProvider clock,
    ILogger<AuthController> logger) : ControllerBase
{
#if DEBUG
    /// <summary>Creates the first development operator when needed, then applies normal sign-in checks.</summary>
    [HttpGet("debug-login")]
    [AllowAnonymous]
    public async Task<IActionResult> DebugLogin(
        [FromServices] IConfiguration configuration,
        [FromServices] EnvironmentBackupService environmentBackup,
        string? returnUrl = "/")
    {
        if (!Security.DebugAutoLoginMiddleware.IsAllowed(HttpContext)) { return NotFound(); }

        var username = configuration["DebugAutoLogin:Username"]!;
        var password = configuration["DebugAutoLogin:Password"]!;
        // Share first-account admission with operator setup and environment restore.
        await environmentBackup.WaitForAdmissionAsync(HttpContext.RequestAborted);
        try
        {
            if (await userManager.FindByNameAsync(username) is null)
            {
                if (EnvironmentRestoreBootstrapper.HasPendingOperation())
                {
                    return Conflict("Wait for the pending environment operation to finish before creating the debug operator.");
                }

                if (await userManager.Users.AnyAsync(user => user.Id != WellKnownUsers.SystemId, HttpContext.RequestAborted))
                {
                    return Conflict("An account already exists. Use the existing operator to manage users.");
                }

                var user = new ApplicationUser { UserName = username, Email = $"{username}@localhost", EmailConfirmed = true, FullName = username };
                await using (var transaction = await identityDb.Database.BeginTransactionAsync(HttpContext.RequestAborted))
                {
                    var created = await userManager.CreateAsync(user, password);
                    var result = created.Succeeded ? await userManager.AddToRoleAsync(user, "Admin") : created;
                    if (!result.Succeeded)
                    {
                        var errors = string.Join(" ", result.Errors.Select(error => error.Description));
                        logger.LogWarning("Debug operator creation failed: {Errors}", errors);
                        return BadRequest(errors);
                    }

                    await transaction.CommitAsync(HttpContext.RequestAborted);
                }
                // Store the existing password-wrapped vault key before the normal unlock provisions user data.
                await vaultDataProtector.SetPasswordAsync(user.Id, configuration["DebugAutoLogin:VaultPassword"]!, HttpContext.RequestAborted);
            }
        }
        finally
        {
            environmentBackup.ReleaseAdmission();
        }

        var outcome = await SignInAsync(username, password, null);
        if (!outcome.Success) { return BadRequest("Debug sign-in failed. Check the local development credentials and account status."); }

        return LocalRedirect(QueryHelpers.AddQueryString("/api/profile/debug-unlock", "returnUrl", LoginNavigation.LocalReturnUrl(returnUrl)));
    }
#endif

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var outcome = await SignInAsync(request.Username, request.Password, null);
        if (outcome.Success) { return Ok(); }

        if (outcome.RetryUntil is not null)
        {
            SetRetryHeader(outcome.RetryUntil.Value);
            return StatusCode(StatusCodes.Status429TooManyRequests, "Try again later.");
        }

        if (outcome.LockedOut) { return StatusCode(423, "Account is locked."); }

        return BadRequest("Invalid username or password.");
    }

    [HttpGet("login-ticket")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWithTicket([FromQuery] string token, [FromQuery] string? returnUrl = "/")
    {
        returnUrl = LoginNavigation.LocalReturnUrl(returnUrl);
        if (string.IsNullOrWhiteSpace(token) || !ticketService.TryConsume(token, out var ticket) || !ticket.IsPasswordLogin || string.IsNullOrWhiteSpace(ticket.Password))
        {
            return RetryRedirect(false, "invalid_ticket", returnUrl);
        }

        var outcome = await SignInAsync(ticket.Username, ticket.Password, null);
        return outcome.Success ? LocalRedirect(returnUrl) : RetryRedirect(false, "invalid_credentials", returnUrl, ticket, outcome);
    }

    [HttpGet("login-pin-ticket")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWithPinTicket([FromQuery] string token, [FromQuery] string? returnUrl = "/")
    {
        returnUrl = LoginNavigation.LocalReturnUrl(returnUrl);
        if (string.IsNullOrWhiteSpace(token) || !ticketService.TryConsume(token, out var ticket) || !ticket.IsPinLogin || string.IsNullOrWhiteSpace(ticket.UserId))
        {
            return RetryRedirect(true, "invalid_ticket", returnUrl);
        }

        var outcome = await SignInAsync(ticket.Username, ticket.Password, ticket.UserId);
        return outcome.Success ? LocalRedirect(returnUrl) : RetryRedirect(true, "invalid_credentials", returnUrl, ticket, outcome);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await DiscardIncognitoThreadsAsync();
        await signInManager.SignOutAsync();
        return Ok();
    }

    [HttpGet("logout-redirect")]
    public async Task<IActionResult> LogoutRedirect(string? expectedUserId)
    {
        if (expectedUserId is not null &&
            !string.Equals(expectedUserId, userManager.GetUserId(User), StringComparison.Ordinal))
        {
            return Redirect("/");
        }

        await DiscardIncognitoThreadsAsync();
        await signInManager.SignOutAsync();
        return Redirect("/");
    }

    private async Task DiscardIncognitoThreadsAsync()
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        await chatSessionService.DiscardIncognitoThreadsAsync(userId, HttpContext.RequestAborted);
    }

    /// <summary>Applies one persisted admission boundary to both credential entry surfaces.</summary>
    private async Task<SignInOutcome> SignInAsync(string? username, string? secret, string? pinUserId)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || string.IsNullOrWhiteSpace(secret)) { return new(); }

        try
        {
            var user = pinUserId is null ? await userManager.FindByNameAsync(username) : await userManager.FindByIdAsync(pinUserId);
            if (user is null)
            {
                if (pinUserId is not null) { return new(); }

                var rejection = throttle.RejectUnknown(userManager.NormalizeName(username)!);
                return new(RetryUntil: rejection.RetryUntil, WasBlocked: rejection.WasBlocked);
            }

            var gate = throttle.GetAccountGate(user.Id);
            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                // Identity can already track this account before the gate is acquired.
                await identityDb.Entry(user).ReloadAsync(HttpContext.RequestAborted);
                if (identityDb.Entry(user).State == EntityState.Detached) { return new(); }

                var retryUntil = await throttle.GetRetryUntilAsync(user.Id, CredentialCategory.Login, HttpContext.RequestAborted);
                if (retryUntil is not null) { return new(RetryUntil: retryUntil, WasBlocked: true); }

                if (await userManager.IsLockedOutAsync(user)) { return new(LockedOut: true); }

                if (!await signInManager.CanSignInAsync(user)) { return new(); }

                var isPin = pinUserId is not null;
                if (isPin && (authSettings.Mode != AuthMode.Home || string.IsNullOrWhiteSpace(user.PinHash) ||
                    !PinPolicy.IsValid(secret) || await userManager.GetTwoFactorEnabledAsync(user))) { return new(); }

                var outcome = await EvaluateCredentialAsync(user, secret, isPin);
                if (outcome.WrongCredential)
                {
                    return outcome with { RetryUntil = await throttle.RecordFailureAsync(user.Id, CredentialCategory.Login, HttpContext.RequestAborted) };
                }

                if (outcome.Success) { await throttle.ResetAsync(user.Id, CredentialCategory.Login, HttpContext.RequestAborted); }

                return outcome;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            // A queued cookie must not escape when required persistence fails.
            Response.Headers.Remove("Set-Cookie");
            logger.LogError(ex, "Sign-in could not complete.");
            return new();
        }
    }

    private async Task<SignInOutcome> EvaluateCredentialAsync(ApplicationUser user, string secret, bool isPin)
    {
        if (isPin)
        {
            if (passwordHasher.VerifyHashedPassword(user, user.PinHash!, secret) != PasswordVerificationResult.Failed)
            {
                await signInManager.SignInAsync(user, isPersistent: true);
                return new(Success: true);
            }
        }
        else if (duressRecords.OwnerSlotOpens(user.Id, user.DuressRecord, secret))
        {
            var ordinary = await signInManager.PasswordSignInAsync(user, secret, isPersistent: true, lockoutOnFailure: false);
            return new(Success: ordinary.Succeeded, LockedOut: ordinary.IsLockedOut);
        }

        var match = isPin
            ? duressRecords.OpenPinSlot(user.Id, user.DuressRecord, secret)
            : duressRecords.OpenPasswordSlot(user.Id, user.DuressRecord, secret);
        if (match.Matched)
        {
            if (await userManager.GetTwoFactorEnabledAsync(user) || match.Actions is null) { return new(); }

            if (!vaultDataProtector.IsAvailable(user.Id))
            {
                pendingDuress.Set(user.Id, match.Actions);
            }
            else if (!await duressActions.ExecuteAsync(user, match.Actions))
            {
                return new();
            }

            if (isPin) { await signInManager.SignInAsync(user, isPersistent: true); }
            else { await signInManager.SignInWithClaimsAsync(user, isPersistent: true, [new Claim("amr", "pwd")]); }

            return new(Success: true);
        }
        // A valid account password with a damaged owner binding must still fail closed.
        if (!isPin && await userManager.CheckPasswordAsync(user, secret)) { return new(); }

        return new(WrongCredential: true);
    }

    private IActionResult RetryRedirect(bool isPin, string error, string returnUrl,
        LoginTicketService.Ticket? ticket = null, SignInOutcome? outcome = null)
    {
        var path = !isPin && authSettings.Mode == AuthMode.Home ? "/login/admin" : "/login";
        var query = new Dictionary<string, string?> { ["e"] = error, ["returnUrl"] = returnUrl };
        if (ticket is { } entry && outcome is not null)
        {
            if (outcome.RetryUntil is not null) { SetRetryHeader(outcome.RetryUntil.Value); }

            query["failure"] = ticketService.CreateFailure(new(entry.Username, entry.UserId, outcome.RetryUntil, outcome.WasBlocked));
        }

        return LocalRedirect(QueryHelpers.AddQueryString(path, query));
    }

    private void SetRetryHeader(DateTimeOffset retryUntil) =>
        Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((retryUntil - clock.GetUtcNow()).TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Keeps admission refusals distinct from evaluated wrong credentials.</summary>
    private sealed record SignInOutcome(bool Success = false, bool LockedOut = false, bool WrongCredential = false,
        DateTimeOffset? RetryUntil = null, bool WasBlocked = false);
}
