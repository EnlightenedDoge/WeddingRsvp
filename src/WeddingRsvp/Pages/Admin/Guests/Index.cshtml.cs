using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Pages.Admin.Guests;

public class IndexModel(AppDbContext db) : PageModel
{
    /// <summary>all | pending | attending | declined | invalid | uninvited | opened</summary>
    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<Guest> Guests { get; private set; } = new();

    public List<SelectListItem> FilterOptions => Filters;

    private static readonly List<SelectListItem> Filters =
    [
        new("הכול", "all"), new("טרם ענו", "pending"), new("פתחו ולא ענו", "opened"),
        new("מגיעים", "attending"), new("לא מגיעים", "declined"),
        new("לא נשלחה הזמנה", "uninvited"), new("מספר לא תקין", "invalid"),
    ];

    public async Task OnGetAsync()
    {
        IQueryable<Guest> q = db.Guests.AsNoTracking();
        q = Filter switch
        {
            "pending" => q.Where(g => g.Status == RsvpStatus.Pending),
            "attending" => q.Where(g => g.Status == RsvpStatus.Attending),
            "declined" => q.Where(g => g.Status == RsvpStatus.Declined),
            "invalid" => q.Where(g => g.PhoneE164 == null),
            "uninvited" => q.Where(g => g.InviteSentAtUtc == null),
            "opened" => q.Where(g => g.Status == RsvpStatus.Pending && g.FirstOpenedAtUtc != null),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Q))
            q = q.Where(g => g.Name.Contains(Q) || g.PhoneRaw.Contains(Q));

        Guests = await q.OrderBy(g => g.Name).ToListAsync();
    }
}
