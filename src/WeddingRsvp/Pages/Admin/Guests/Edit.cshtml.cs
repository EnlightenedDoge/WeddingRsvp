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

public class EditModel(AppDbContext db, CampaignService campaign, IsraelClock clock, IOptions<AppOptions> app) : PageModel
{
    public sealed class GuestInput
    {
        [Required(ErrorMessage = "חסר שם")] public string Name { get; set; } = "";
        public string? Phone { get; set; }
        [Range(1, 50, ErrorMessage = "בין 1 ל-50")] public int InvitedCount { get; set; } = 1;
        public RsvpStatus Status { get; set; }
        [Range(0, 50)] public int AttendingCount { get; set; }
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
            Name = g.Name, Phone = g.PhoneRaw, InvitedCount = g.InvitedCount,
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

        if (Input.Status == RsvpStatus.Attending && (Input.AttendingCount < 1 || Input.AttendingCount > Input.InvitedCount))
            ModelState.AddModelError("Input.AttendingCount", "מספר המגיעים צריך להיות בין 1 למספר המוזמנים");

        var phone = PhoneNormalizer.Normalize(Input.Phone);
        if (phone.IsValid && await db.Guests.AnyAsync(x => x.PhoneE164 == phone.E164 && x.Id != g.Id))
            ModelState.AddModelError("Input.Phone", "המספר כבר שייך לאורח אחר");

        if (!ModelState.IsValid) return Page();

        g.Name = Input.Name.Trim();
        g.PhoneRaw = Input.Phone?.Trim() ?? "";
        g.PhoneE164 = phone.E164;
        g.PhoneError = phone.Error;
        g.InvitedCount = Input.InvitedCount;
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
        TempData["Flash"] = "נשמר";
        return RedirectToPage(new { id = g.Id });
    }

    public async Task<IActionResult> OnPostSendInviteAsync(int id)
    {
        var missing = await campaign.MissingFieldsAsync(MessageKind.Invite);
        TempData["Flash"] = missing.Count > 0
            ? "אי אפשר לשלוח: חסרים פרטים באירוע (" + string.Join(" ", missing) + ")"
            : await campaign.QueueInviteAsync(id)
                ? "ההזמנה נכנסה לתור השליחה"
                : "אי אפשר לשלוח: מספר לא תקין";
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
        TempData["Flash"] = "האורח נמחק";
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
