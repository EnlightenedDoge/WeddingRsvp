using Microsoft.AspNetCore.Identity;

namespace WeddingRsvp.Domain;

public enum UserRole { Couple = 0, SiteAdmin = 1 }

public class AppUser : IdentityUser
{
    public UserRole Role { get; set; } = UserRole.Couple;

    /// <summary>Set for <see cref="UserRole.Couple"/>, null for the site admin.</summary>
    public int? CoupleId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
