using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Services;

public sealed class SettingsService(AppDbContext db, ITenantContext tenant)
{
    /// <summary>Returns the current couple's tracked settings row, creating it empty on first use.</summary>
    public async Task<EventSettings> GetAsync(CancellationToken ct = default)
    {
        var coupleId = tenant.CoupleId ?? throw new InvalidOperationException("No tenant is set.");
        var s = await db.EventSettings.FirstOrDefaultAsync(ct);
        if (s is not null) return s;

        s = new EventSettings { CoupleId = coupleId };
        db.EventSettings.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }
}
