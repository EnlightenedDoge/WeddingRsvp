using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public sealed class SettingsService(AppDbContext db)
{
    /// <summary>Returns the tracked singleton row, creating it with defaults on first use.</summary>
    public async Task<EventSettings> GetAsync(CancellationToken ct = default)
    {
        var s = await db.EventSettings.FirstOrDefaultAsync(x => x.Id == EventSettings.SingletonId, ct);
        if (s is not null) return s;

        s = new EventSettings();
        db.EventSettings.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }
}
