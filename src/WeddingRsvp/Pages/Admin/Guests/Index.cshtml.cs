using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Pages.Admin.Guests;

public class IndexModel(AppDbContext db, Services.Ui ui) : PageModel
{
    /// <summary>all | pending | attending | declined | invalid | uninvited | opened</summary>
    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<Guest> Guests { get; private set; } = new();

    public List<SelectListItem> FilterOptions =>
    [
        new(ui.T("הכול", "All"), "all"), new(ui.T("טרם ענו", "Awaiting"), "pending"),
        new(ui.T("פתחו ולא ענו", "Opened, not answered"), "opened"),
        new(ui.T("מגיעים", "Attending"), "attending"), new(ui.T("לא מגיעים", "Declined"), "declined"),
        new(ui.T("לא נשלחה הזמנה", "Not invited yet"), "uninvited"), new(ui.T("מספר לא תקין", "Invalid number"), "invalid"),
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
