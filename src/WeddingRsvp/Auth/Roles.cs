using System.Security.Claims;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Auth;

public static class Roles
{
    public const string SiteAdmin = "SiteAdmin";
    public const string Couple = "Couple";
    public const string CoupleIdClaim = "couple_id";

    public static int? CoupleId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(CoupleIdClaim), out var id) ? id : null;
}
