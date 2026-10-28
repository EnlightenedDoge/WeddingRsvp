using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services;

public sealed record NewCouple(
    string DisplayName, string Email, string CoupleNames = "", DateTime? StartsAtLocal = null,
    string VenueName = "", string VenueAddress = "", string SmsSenderName = "");

public sealed record CreateCoupleResult(bool Ok, Couple? Couple, string? SetPasswordLink, string? Error);

/// <summary>What the site admin does to couple accounts. Runs without a tenant (it spans couples).</summary>
public sealed class CoupleAdminService(
    AppDbContext db, UserManager<AppUser> users, IsraelClock clock, IOptions<AppOptions> app, AuditLog audit)
{
    public async Task<CreateCoupleResult> CreateAsync(NewCouple n, AppUser actor, CancellationToken ct = default)
    {
        var email = n.Email.Trim();
        if (string.IsNullOrWhiteSpace(n.DisplayName) || !IsPlausibleEmail(email))
            return new(false, null, null, "invalid");
        if (await users.FindByEmailAsync(email) is not null)
            return new(false, null, null, "email-taken");

        var couple = new Couple { DisplayName = n.DisplayName.Trim(), CreatedAtUtc = clock.UtcNow };
        db.Couples.Add(couple);
        await db.SaveChangesAsync(ct);

        // Details the site admin knew at creation. Everything else stays empty: nothing is assumed.
        db.EventSettings.Add(new EventSettings
        {
            CoupleId = couple.Id,
            CoupleNames = n.CoupleNames.Trim(),
            StartsAtLocal = n.StartsAtLocal,
            VenueName = n.VenueName.Trim(),
            VenueAddress = n.VenueAddress.Trim(),
            SmsSenderName = n.SmsSenderName.Trim(),
        });

        // No password yet: the couple chooses one through the one-time link.
        var user = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true,
            Role = UserRole.Couple, CoupleId = couple.Id, CreatedAtUtc = clock.UtcNow,
        };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
        {
            db.Couples.Remove(couple); // cascades the settings row
            await db.SaveChangesAsync(ct);
            return new(false, null, null, "invalid");
        }

        await audit.WriteAsync("couple.created", actor, couple.Id);
        return new(true, couple, await IssueSetPasswordLinkAsync(user, actor), null);
    }

    /// <summary>A fresh one-time link replaces any earlier one (the token is tied to the security stamp).</summary>
    public async Task<string> IssueSetPasswordLinkAsync(AppUser user, AppUser actor)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        await audit.WriteAsync("couple.link-issued", actor, user.CoupleId);
        return SetPasswordLink(user, token);
    }

    private string SetPasswordLink(AppUser user, string token) =>
        $"{app.Value.PublicBaseUrl.TrimEnd('/')}/set-password?u={Uri.EscapeDataString(user.Id)}" +
        $"&t={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))}";

    public static string DecodeToken(string t) => Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(t));

    public async Task SetSuspendedAsync(int coupleId, bool suspended, AppUser actor, CancellationToken ct = default)
    {
        var couple = await db.Couples.FirstOrDefaultAsync(c => c.Id == coupleId && c.DeletedAtUtc == null, ct);
        if (couple is null || couple.Suspended == suspended) return;
        couple.Suspended = suspended;
        await db.SaveChangesAsync(ct);

        // Invalidate existing sessions so suspension takes effect immediately.
        foreach (var u in await db.Users.Where(u => u.CoupleId == coupleId).ToListAsync(ct))
            await users.UpdateSecurityStampAsync(u);
        await audit.WriteAsync(suspended ? "couple.suspended" : "couple.unsuspended", actor, coupleId);
    }

    public Task<AppUser?> PrimaryUserAsync(int coupleId, CancellationToken ct = default) =>
        db.Users.Where(u => u.CoupleId == coupleId).OrderBy(u => u.CreatedAtUtc).FirstOrDefaultAsync(ct);

    private static bool IsPlausibleEmail(string e) =>
        e.Length is > 3 and <= 254 && e.Contains('@') && !e.Contains(' ')
        && e.IndexOf('@') == e.LastIndexOf('@') && e.LastIndexOf('.') > e.IndexOf('@');
}
