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

    /// <summary>guests | families</summary>
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "guests";
    public bool ShowFamilies => View == "families";

    public List<Guest> Guests { get; private set; } = new();

    /// <summary>One family for the Families tab. The members all hold the same answer, so any one of them shows it.</summary>
    public sealed record FamilyRow(Family Family, List<Guest> Members)
    {
        /// <summary>The identifier the couple gave (import column), or the generated F&lt;id&gt; that the CSV export uses.</summary>
        public string Identifier => Family.ExternalId ?? "F" + Family.Id;
        /// <summary>People the family is invited with together; null when a member has no limit.</summary>
        public int? Invited => Members.Any(m => m.NoGuestLimit) ? null : Members.Sum(m => m.InvitedCount);
        public Guest Answer => Members.OrderByDescending(m => m.RespondedAtUtc).First();
    }
    public List<FamilyRow> Families { get; private set; } = new();

    public List<SelectListItem> FilterOptions =>
    [
        new(ui.T("הכול", "All"), "all"), new(ui.T("טרם ענו", "Awaiting"), "pending"),
        new(ui.T("פתחו ולא ענו", "Opened, not answered"), "opened"),
        new(ui.T("מגיעים", "Attending"), "attending"), new(ui.T("לא מגיעים", "Declined"), "declined"),
        new(ui.T("לא נשלחה הזמנה", "Not invited yet"), "uninvited"), new(ui.T("מספר לא תקין", "Invalid number"), "invalid"),
    ];

    public async Task OnGetAsync()
    {
        if (ShowFamilies) { await LoadFamiliesAsync(); return; }
        IQueryable<Guest> q = db.Guests.AsNoTracking().Include(g => g.Family);
        q = Filter switch
        {
            "pending" => q.Where(g => g.Status == RsvpStatus.Pending),
            "attending" => q.Where(g => g.Status == RsvpStatus.Attending),
            "declined" => q.Where(g => g.Status == RsvpStatus.Declined),
            "invalid" => q.Where(g => g.PhoneE164 == null && g.PhoneError != null),
            "uninvited" => q.Where(g => g.InviteSentAtUtc == null && g.GroupLinkId == null),
            "opened" => q.Where(g => g.Status == RsvpStatus.Pending && g.FirstOpenedAtUtc != null),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Q))
            q = q.Where(g => g.Name.Contains(Q) || g.PhoneRaw.Contains(Q));

        Guests = await q.OrderBy(g => g.Name).ToListAsync();
        FamilyCount = await db.Families.CountAsync();
    }

    public int FamilyCount { get; private set; }

    private async Task LoadFamiliesAsync()
    {
        var members = await db.Guests.AsNoTracking().Include(g => g.Family).Where(g => g.FamilyId != null).OrderBy(g => g.Name).ToListAsync();
        Families = members.GroupBy(g => g.Family!)
            .Select(x => new FamilyRow(x.Key, x.ToList()))
            .Where(f => string.IsNullOrWhiteSpace(Q)
                || f.Family.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) || f.Identifier.Contains(Q, StringComparison.OrdinalIgnoreCase)
                || f.Members.Any(g => g.Name.Contains(Q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Family.Name).ThenBy(f => f.Family.Id).ToList();
        FamilyCount = Families.Count;
    }
}
