using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace WeddingRsvp.Services;

/// <summary>What a couple can do to their own account. Runs in the couple's tenant.</summary>
public sealed class AccountService(
    AppDbContext db, UserManager<AppUser> users, ITenantContext tenant, IsraelClock clock,
    IOptions<AppOptions> app, AuditLog audit)
{
    /// <summary>
    /// "Delete all information": removes guests, messages, settings, uploaded images and every user of the couple.
    /// Only a tombstone row (id + timestamps, name cleared) remains so the audit log still has something to point at.
    /// </summary>
    public async Task DeleteAllAsync(AppUser actor, CancellationToken ct = default)
    {
        var coupleId = tenant.CoupleId ?? throw new InvalidOperationException("No tenant is set.");

        await db.Messages.ExecuteDeleteAsync(ct); // tenant filter applies: only this couple's rows
        await db.Guests.ExecuteDeleteAsync(ct);
        await db.Families.ExecuteDeleteAsync(ct);
        await db.GroupLinks.ExecuteDeleteAsync(ct);
        await db.PhoneVerifications.ExecuteDeleteAsync(ct);
        await db.EventSettings.ExecuteDeleteAsync(ct);

        var mediaDir = Path.Combine(Path.GetFullPath(app.Value.DataDir), "media", coupleId.ToString());
        if (Directory.Exists(mediaDir)) Directory.Delete(mediaDir, recursive: true);

        var couple = await db.Couples.FirstAsync(c => c.Id == coupleId, ct);
        couple.DisplayName = "";
        couple.Suspended = true;
        couple.DeletedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("couple.deleted-all-data", actor, coupleId);
        foreach (var u in await db.Users.Where(u => u.CoupleId == coupleId).ToListAsync(ct))
            await users.DeleteAsync(u);
    }
}
