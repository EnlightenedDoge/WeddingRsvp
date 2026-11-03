using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Pages.Admin.Guests;

public class IndexModel(AppDbContext db, Services.Ui ui, Services.FamilyService families, Services.IsraelClock clock, Services.GuestBulkService bulk) : PageModel
{
    /// <summary>all | pending | attending | declined | invalid | uninvited | opened</summary>
    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>name | surname | family | status | sent</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    /// <summary>asc | desc</summary>
    [BindProperty(SupportsGet = true)] public string Dir { get; set; } = "asc";
    public bool Desc => Dir == "desc";

    /// <summary>The list's own URL with another sort; choosing the column that is already sorted flips the direction.</summary>
    public string SortUrl(string column) =>
        $"/admin/guests?sort={column}&dir={(Sort == column && !Desc ? "desc" : "asc")}&filter={Uri.EscapeDataString(Filter)}&q={Uri.EscapeDataString(Q ?? "")}";
    public string SortMark(string column) => Sort != column ? "" : Desc ? " ▼" : " ▲";

    /// <summary>guests | families</summary>
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "guests";
    public bool ShowFamilies => View == "families";

    public List<Guest> Guests { get; private set; } = new();

    /// <summary>One family for the Families tab. The members all hold the same answer, so any one of them shows it.</summary>
    public sealed record FamilyRow(Family Family, List<Guest> Members)
    {
        /// <summary>The identifier the couple gave (import column), or the generated F&lt;id&gt; that the CSV export uses.</summary>
        public string Identifier => Family.DisplayId;
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

        Guests = await Sorted(q).ToListAsync();
        if (Sort == "surname") // the last word of the name: sorted here because it cannot be expressed in the query
        {
            var cmp = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);
            var ordered = Desc ? Guests.OrderByDescending(g => g.Surname, cmp) : Guests.OrderBy(g => g.Surname, cmp);
            Guests = ordered.ThenBy(g => g.Name, cmp).ThenBy(g => g.Id).ToList();
        }
        FamilyOptions = await families.OptionsAsync();
        FamilyCount = await db.Families.CountAsync();
    }

    public int FamilyCount { get; private set; }

    /// <summary>The chosen sort, always ending with the name (then the id) so the order is stable. Guests without a family, or not yet invited, come last when ascending.</summary>
    private IQueryable<Guest> Sorted(IQueryable<Guest> q)
    {
        IOrderedQueryable<Guest> o = (Sort, Desc) switch
        {
            // Family group: by family name, then by the group itself (identifier, then id), so the members of one group stay together
            // even when two groups share a name.
            ("family", false) => q.OrderBy(g => g.Family == null).ThenBy(g => g.Family!.Name).ThenBy(g => g.Family!.ExternalId).ThenBy(g => g.FamilyId),
            ("family", true) => q.OrderBy(g => g.Family != null).ThenByDescending(g => g.Family!.Name).ThenByDescending(g => g.Family!.ExternalId).ThenByDescending(g => g.FamilyId),
            ("status", false) => q.OrderBy(g => g.Status),
            ("status", true) => q.OrderByDescending(g => g.Status),
            ("sent", false) => q.OrderBy(g => g.InviteSentAtUtc == null).ThenBy(g => g.InviteSentAtUtc),
            ("sent", true) => q.OrderBy(g => g.InviteSentAtUtc != null).ThenByDescending(g => g.InviteSentAtUtc),
            (_, true) => q.OrderByDescending(g => g.Name),
            _ => q.OrderBy(g => g.Name),
        };
        return o.ThenBy(g => g.Name).ThenBy(g => g.Id);
    }
    public List<Services.FamilyService.FamilyOption> FamilyOptions { get; private set; } = new();

    /// <summary>The bulk bar on the Guests list: invite | invited | family | unfamily | export | delete, applied to the ticked guests.</summary>
    public async Task<IActionResult> OnPostBulkAsync(int[] ids, string? action, int familyId, string? familyName, int invited, bool noLimit, string? returnUrl)
    {
        var back = LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin/guests");
        ids = ids.Distinct().ToArray();
        if (ids.Length == 0) { TempData["Flash"] = ui.T("לא נבחרו אורחים", "No guests selected"); return back; }
        switch (action)
        {
            case "export":
                var chosen = await db.Guests.AsNoTracking().Include(g => g.Family).Where(g => ids.Contains(g.Id)).OrderBy(g => g.Name).ToListAsync();
                return File(Services.GuestCsv.Build(chosen), "text/csv; charset=utf-8", "guests-selected.csv");
            case "invite":
                var r = await bulk.SendInvitesAsync(ids);
                TempData["Flash"] = r.MissingFields.Count > 0
                    ? ui.T("אי אפשר לשלוח: חסרים פרטים באירוע (", "Can't send: event details are missing (") + string.Join(" ", r.MissingFields) + ")"
                    : ui.T($"{r.Queued} הזמנות נכנסו לתור. דולגו: {r.AlreadyInvited} כבר הוזמנו, {r.NoPhone} בלי מספר תקין, {r.GroupLink} דרך קישור קבוצתי.",
                           $"{r.Queued} invitations queued. Skipped: {r.AlreadyInvited} already invited, {r.NoPhone} without a valid number, {r.GroupLink} via group link.");
                break;
            case "invited":
                var n = await bulk.SetInvitedAsync(ids, noLimit ? 0 : invited);
                TempData["Flash"] = n < 0 ? ui.T("מספר מוזמנים: בין 1 ל-50, או ללא הגבלה", "People invited: 1 to 50, or no limit")
                    : ui.T($"מספר המוזמנים עודכן ל-{n} אורחים", $"People invited updated for {n} guests");
                break;
            case "family":
            case "unfamily":
                var done = await bulk.SetFamilyAsync(ids, action == "unfamily" ? Services.FamilyService.None : familyId, familyName);
                TempData["Flash"] = done < 0 ? ui.T("נא לבחור משפחה, או להזין שם למשפחה חדשה", "Choose a family, or enter a name for a new one")
                    : action == "unfamily" ? ui.T($"{done} אורחים הוצאו ממשפחה", $"{done} guests taken out of their family")
                    : ui.T($"{done} אורחים הוכנסו למשפחה", $"{done} guests put in the family");
                break;
            case "delete":
                var deleted = await bulk.DeleteAsync(ids);
                TempData["Flash"] = ui.T($"{deleted} אורחים נמחקו", $"{deleted} guests deleted");
                break;
            default:
                TempData["Flash"] = ui.T("נא לבחור פעולה", "Choose an action");
                break;
        }
        return back;
    }

    /// <summary>Quick edit from a list (also used by the dashboard): attending people and dietary needs of one guest.</summary>
    public async Task<IActionResult> OnPostQuickEditAsync(int id, int adults, int children, int vegetarian, int vegan, int glutenFree, string? note, string? returnUrl)
    {
        var result = await families.EditAttendanceAsync(id, adults, children,
            new Services.DietaryAnswer(vegetarian, vegan, glutenFree, note), clock.UtcNow);
        TempData["Flash"] = result switch
        {
            Services.AttendanceEdit.Saved => ui.T("נשמר", "Saved"),
            Services.AttendanceEdit.NotAttending => ui.T("אפשר לעדכן כמות רק לאורח שאישר הגעה", "Only a guest who confirmed can be edited here"),
            Services.AttendanceEdit.InvalidCount => ui.T("לפחות מבוגר אחד, ובסך הכול לא יותר ממספר המוזמנים", "At least 1 adult, and no more than the number invited in total"),
            Services.AttendanceEdit.InvalidDiet => ui.T($"העדפות תזונה: מספרים בין 0 למספר המגיעים, והערה עד {Guest.DietNoteMax} תווים", $"Dietary needs: numbers from 0 up to the people attending, and a note of at most {Guest.DietNoteMax} characters"),
            _ => ui.T("האורח לא נמצא", "Guest not found"),
        };
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin/guests");
    }

    private async Task LoadFamiliesAsync()
    {
        var members = await db.Guests.AsNoTracking().Include(g => g.Family).Where(g => g.FamilyId != null).OrderBy(g => g.Name).ToListAsync();
        Families = members.GroupBy(g => g.FamilyId!.Value) // not by Family: untracked queries give each member its own instance
            .Select(x => new FamilyRow(x.First().Family!, x.ToList()))
            .Where(f => string.IsNullOrWhiteSpace(Q)
                || f.Family.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) || f.Identifier.Contains(Q, StringComparison.OrdinalIgnoreCase)
                || f.Members.Any(g => g.Name.Contains(Q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Family.Name).ThenBy(f => f.Family.Id).ToList();
        FamilyCount = Families.Count;
    }
}
