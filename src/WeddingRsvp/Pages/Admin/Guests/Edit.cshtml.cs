using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin.Guests;

public class EditModel(AppDbContext db, CampaignService campaign, IsraelClock clock, IOptions<AppOptions> app, Ui ui) : PageModel
{
    public sealed class GuestInput
    {
        public string Name { get; set; } = "";
        public string? Phone { get; set; }
        public int InvitedCount { get; set; } = 1;
        /// <summary>No limit on how many people the invitation covers (stored as InvitedCount 0).</summary>
        public bool NoLimit { get; set; }
        public RsvpStatus Status { get; set; }
        public int AttendingCount { get; set; }
        public string? Notes { get; set; }
    }

    [BindProperty] public GuestInput Input { get; set; } = new();
    public Guest? Existing { get; private set; }
    public string? RsvpLink { get; private set; }
    public List<MessageLog> Messages { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();
        if (!await LoadAsync(id.Value)) return NotFound();

        var g = Existing!;
        Input = new GuestInput
        {
            Name = g.Name, Phone = g.PhoneRaw, InvitedCount = g.NoGuestLimit ? 1 : g.InvitedCount, NoLimit = g.NoGuestLimit,
            Status = g.Status, AttendingCount = g.AttendingCount, Notes = g.Notes,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        Guest g;
        if (id is null)
        {
            g = new Guest { Token = await NewUniqueTokenAsync(), CreatedAtUtc = clock.UtcNow };
            db.Guests.Add(g);
        }
        else
        {
            if (!await LoadAsync(id.Value)) return NotFound();
            g = Existing!;
        }

        if (string.IsNullOrWhiteSpace(Input.Name))
            ModelState.AddModelError("Input.Name", ui.T("חסר שם", "Name is required"));
        // "No guest limit" is the toggle, or 0 typed in the number box; both are stored as 0.
        var unlimited = Input.NoLimit || Input.InvitedCount == 0;
        if (!unlimited && Input.InvitedCount is < 1 or > 50)
            ModelState.AddModelError("Input.InvitedCount", ui.T("בין 1 ל-50, או סמנו \"ללא הגבלה\"", "Between 1 and 50, or turn on \"No guest limit\""));
        var maxAttending = unlimited ? Guest.UnlimitedCap : Input.InvitedCount;
        if (Input.Status == RsvpStatus.Attending && (Input.AttendingCount < 1 || Input.AttendingCount > maxAttending))
            ModelState.AddModelError("Input.AttendingCount", unlimited
                ? ui.T($"מספר המגיעים צריך להיות בין 1 ל-{Guest.UnlimitedCap}", $"Attending must be between 1 and {Guest.UnlimitedCap}")
                : ui.T("מספר המגיעים צריך להיות בין 1 למספר המוזמנים", "Attending must be between 1 and the number invited"));

        var phone = PhoneNormalizer.Normalize(Input.Phone);
        if (phone.IsValid && await db.Guests.AnyAsync(x => x.PhoneE164 == phone.E164 && x.Id != g.Id))
            ModelState.AddModelError("Input.Phone", ui.T("המספר כבר שייך לאורח אחר", "This number already belongs to another guest"));

        if (!ModelState.IsValid) return Page();

        g.Name = Input.Name.Trim();
        g.PhoneRaw = Input.Phone?.Trim() ?? "";
        g.PhoneE164 = phone.E164;
        g.PhoneError = phone.Error;
        g.InvitedCount = unlimited ? 0 : Input.InvitedCount;
        g.Notes = Input.Notes;

        // A manual status change is recorded as such, so the couple can tell phone-call
        // answers apart from guests who answered through the link.
        if (g.Status != Input.Status || (Input.Status == RsvpStatus.Attending && g.AttendingCount != Input.AttendingCount))
        {
            g.Status = Input.Status;
            g.AttendingCount = Input.Status == RsvpStatus.Attending ? Input.AttendingCount : 0;
            g.Source = Input.Status == RsvpStatus.Pending ? RsvpSource.None : RsvpSource.Manual;
            g.RespondedAtUtc = Input.Status == RsvpStatus.Pending ? null : clock.UtcNow;
        }

        await db.SaveChangesAsync();
        TempData["Flash"] = ui.T("נשמר", "Saved");
        return RedirectToPage(new { id = g.Id });
    }

    public async Task<IActionResult> OnPostSendInviteAsync(int id)
    {
        var missing = await campaign.MissingFieldsAsync(MessageKind.Invite);
        TempData["Flash"] = missing.Count > 0
            ? ui.T("אי אפשר לשלוח: חסרים פרטים באירוע (", "Can't send: event details are missing (") + string.Join(" ", missing) + ")"
            : await campaign.QueueInviteAsync(id)
                ? ui.T("ההזמנה נכנסה לתור השליחה", "The invitation was queued")
                : ui.T("אי אפשר לשלוח: מספר לא תקין", "Can't send: invalid phone number");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var g = await db.Guests.FindAsync(id);
        if (g is not null)
        {
            db.Guests.Remove(g);
            await db.SaveChangesAsync();
        }
        TempData["Flash"] = ui.T("האורח נמחק", "Guest deleted");
        return RedirectToPage("Index");
    }

    private async Task<bool> LoadAsync(int id)
    {
        Existing = await db.Guests.FirstOrDefaultAsync(x => x.Id == id);
        if (Existing is null) return false;
        RsvpLink = MessageTemplates.GuestLink(app.Value.PublicBaseUrl, Existing.Token);
        Messages = await db.Messages.AsNoTracking()
            .Where(m => m.GuestId == id).OrderByDescending(m => m.Id).ToListAsync();
        return true;
    }

    private async Task<string> NewUniqueTokenAsync()
    {
        string t;
        do t = TokenGenerator.New(); while (await db.Guests.AnyAsync(g => g.Token == t));
        return t;
    }
}
