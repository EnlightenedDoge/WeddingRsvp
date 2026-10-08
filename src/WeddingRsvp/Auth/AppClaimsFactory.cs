using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Auth;

/// <summary>Puts the role and the couple id into the sign-in cookie; the tenant is read from there.</summary>
public sealed class AppClaimsFactory(UserManager<AppUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim(ClaimTypes.Role, user.Role == UserRole.SiteAdmin ? Roles.SiteAdmin : Roles.Couple));
        if (user is { Role: UserRole.Couple, CoupleId: { } cid })
            id.AddClaim(new Claim(Roles.CoupleIdClaim, cid.ToString()));
        return id;
    }
}
