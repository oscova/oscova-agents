using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Oscova.Agents.Infrastructure.Auth;
using System.Security.Claims;

namespace Oscova.Agents.Host.Security;

public sealed class AppUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        // Mirror FullName as the display name claim when present
        if (!string.IsNullOrWhiteSpace(user.FullName))
        {
            // Remove any existing name claims to avoid duplicates
            foreach (var existing in identity.FindAll(ClaimTypes.Name).ToList())
            {
                identity.RemoveClaim(existing);
            }

            identity.AddClaim(new Claim(ClaimTypes.Name, user.FullName));
        }

        return identity;
    }
}
