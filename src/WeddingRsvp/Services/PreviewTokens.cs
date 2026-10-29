using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;

namespace WeddingRsvp.Services;

/// <summary>
/// The token behind a couple's demo link (test messages and the Event page preview point at it). Every couple has one from the
/// moment their settings exist; tokens share the guest-token namespace, so a demo link can never collide with a guest's.
/// </summary>
public static class PreviewTokens
{
    public static async Task<string> NewAsync(AppDbContext db, CancellationToken ct = default)
    {
        string t;
        do t = TokenGenerator.New();
        while (await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.Token == t, ct)
               || await db.EventSettings.IgnoreQueryFilters().AnyAsync(x => x.PreviewToken == t, ct)
               || db.ChangeTracker.Entries<Domain.EventSettings>().Any(e => e.Entity.PreviewToken == t));
        return t;
    }

    /// <summary>Gives a token to settings rows that predate it (couples that existed before the demo link). Safe to run on every start.</summary>
    public static async Task<int> BackfillAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.EventSettings.IgnoreQueryFilters().Where(s => s.PreviewToken == "").ToListAsync(ct);
        foreach (var s in rows)
        {
            s.PreviewToken = await NewAsync(db, ct);
            await db.SaveChangesAsync(ct); // one at a time: each new token is checked against the ones already saved
        }
        return rows.Count;
    }
}
