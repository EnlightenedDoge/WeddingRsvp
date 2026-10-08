using System.Security.Claims;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Auth;

public static class Roles
{
    public const string SiteAdmin = "SiteAdmin";
    public const string Couple = "Couple";
    public const string CoupleIdClaim = "couple_id";
    /// <summary>Present only while a site admin is working inside a couple's account ("open as couple").</summary>
    public const string ActingClaim = "acting_as_couple";

    public static bool IsActingAsCouple(this ClaimsPrincipal user) => user.HasClaim(ActingClaim, "1");

    public static int? CoupleId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(CoupleIdClaim), out var id) ? id : null;
}
