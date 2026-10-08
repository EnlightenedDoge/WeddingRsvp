using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;

namespace WeddingRsvp.Pages.SiteAdmin;

public sealed record CoupleRow(int Id, string DisplayName, string? Email, int Guests, bool Suspended, bool HasPassword);

public class CouplesModel(AppDbContext db) : PageModel
{
    public List<CoupleRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        // Site-admin view across tenants: guest counts bypass the tenant filter on purpose (counts only, never rows).
        var guestCounts = await db.Guests.IgnoreQueryFilters()
            .GroupBy(g => g.CoupleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var owners = await db.Users.Where(u => u.CoupleId != null)
            .Select(u => new { u.CoupleId, u.Email, HasPassword = u.PasswordHash != null }).ToListAsync();
        var couples = await db.Couples.Where(c => c.DeletedAtUtc == null).OrderByDescending(c => c.Id).ToListAsync();

        Rows = couples.Select(c =>
        {
            var o = owners.FirstOrDefault(x => x.CoupleId == c.Id);
            return new CoupleRow(c.Id, c.DisplayName, o?.Email, guestCounts.GetValueOrDefault(c.Id),
                c.Suspended, o?.HasPassword ?? false);
        }).ToList();
    }
}
