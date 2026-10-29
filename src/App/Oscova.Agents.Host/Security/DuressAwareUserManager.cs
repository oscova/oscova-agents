using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Secrets;

namespace Oscova.Agents.Host.Security;

/// <summary>Keeps the duress record in step with every account-password write.</summary>
public sealed class DuressAwareUserManager(
    DuressRecordProtector duressRecords,
    SignInThrottle throttle,
    ApplicationDbContext identityDb,
    IUserStore<ApplicationUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IEnumerable<IUserValidator<ApplicationUser>> userValidators,
    IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<ApplicationUser>> logger)
    : UserManager<ApplicationUser>(
        store,
        optionsAccessor,
        passwordHasher,
        userValidators,
        passwordValidators,
        keyNormalizer,
        errors,
        services,
        logger)
{
    /// <inheritdoc />
    public override Task<IdentityResult> CreateAsync(ApplicationUser user, string password)
    {
        user.DuressRecord = duressRecords.Create(user.Id, password);
        return base.CreateAsync(user, password);
    }

    /// <inheritdoc />
    public override async Task<IdentityResult> ChangePasswordAsync(
        ApplicationUser user,
        string currentPassword,
        string newPassword)
    {
        var gate = throttle.GetAccountGate(user.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await identityDb.Entry(user).ReloadAsync().ConfigureAwait(false);
            if (identityDb.Entry(user).State == EntityState.Detached) { return IdentityResult.Failed(ErrorDescriber.InvalidToken()); }

            if (!await CheckPasswordAsync(user, currentPassword).ConfigureAwait(false))
            {
                return IdentityResult.Failed(ErrorDescriber.PasswordMismatch());
            }

            if (duressRecords.OpenPasswordSlot(user.Id, user.DuressRecord, newPassword).Matched)
            {
                return DuressPasswordConflict();
            }

            var previous = user.DuressRecord;
            user.DuressRecord = duressRecords.ResealOwnerSlot(user.Id, previous, newPassword);
            var result = await base.ChangePasswordAsync(user, currentPassword, newPassword).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                user.DuressRecord = previous;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<IdentityResult> ResetPasswordAsync(
        ApplicationUser user,
        string token,
        string newPassword)
    {
        var gate = throttle.GetAccountGate(user.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await identityDb.Entry(user).ReloadAsync().ConfigureAwait(false);
            if (identityDb.Entry(user).State == EntityState.Detached) { return IdentityResult.Failed(ErrorDescriber.InvalidToken()); }

            if (!await VerifyUserTokenAsync(
                user,
                Options.Tokens.PasswordResetTokenProvider,
                ResetPasswordTokenPurpose,
                token).ConfigureAwait(false))
            {
                return IdentityResult.Failed(ErrorDescriber.InvalidToken());
            }

            if (duressRecords.OpenPasswordSlot(user.Id, user.DuressRecord, newPassword).Matched)
            {
                return DuressPasswordConflict();
            }

            var previous = user.DuressRecord;
            user.DuressRecord = duressRecords.ResealOwnerSlot(user.Id, previous, newPassword);
            var result = await base.ResetPasswordAsync(user, token, newPassword).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                user.DuressRecord = previous;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static IdentityResult DuressPasswordConflict() => IdentityResult.Failed(new IdentityError
    {
        Code = "PasswordNotAllowed",
        Description = "The new password cannot be used. Choose a different password."
    });
}
