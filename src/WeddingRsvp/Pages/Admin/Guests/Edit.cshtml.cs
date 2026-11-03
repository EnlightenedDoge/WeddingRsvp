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

public class EditModel(AppDbContext db, CampaignService campaign, IsraelClock clock, IOptions<AppOptions> app, Ui ui, FamilyService families) : PageModel
{
    public sealed class GuestInput
    {
        public string Name { get; set; } = "";
        public string? Phone { get; set; }
        public int InvitedCount { get; set; } = 1;
        /// <summary>No limit on how many people the invitation covers (stored as InvitedCount 0).</summary>
        public bool NoLimit { get; set; }
        public RsvpStatus Status { get; set; }
        /// <summary>Adults and children attending; they only apply while the status is Attending (default: 1 adult, 0 children).</summary>
        public int AttendingAdults { get; set; } = 1;
        public int AttendingChildren { get; set; }
        public string? Notes { get; set; }
        /// <summary>0 = no family, -1 = a new family named <see cref="FamilyName"/>, otherwise an existing family's id.</summary>
        public int FamilyId { get; set; }
        /// <summary>The new family's name, or a new name for the selected family.</summary>
        public string? FamilyName { get; set; }
        public int DietVegetarian { get; set; }
        public int DietVegan { get; set; }
        public int DietGlutenFree { get; set; }
        public string? DietNote { get; set; }
    }

    [BindProperty] public GuestInput Input { get; set; } = new();
    public Guest? Existing { get; private set; }
    public string? RsvpLink { get; private set; }
    public List<FamilyService.FamilyOption> Families { get; private set; } = new();
    public List<MessageLog> Messages { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();
        if (!await LoadAsync(id.Value)) return NotFound();

        var g = Existing!;
        Input = new GuestInput
        {
            Name = g.Name, Phone = g.PhoneRaw, InvitedCount = g.NoGuestLimit ? 1 : g.InvitedCount, NoLimit = g.NoGuestLimit,
            Status = g.Status, Notes = g.Notes, FamilyId = g.FamilyId ?? 0, FamilyName = g.Family?.Name,
            DietVegetarian = g.DietVegetarian, DietVegan = g.DietVegan, DietGlutenFree = g.DietGlutenFree, DietNote = g.DietNote,
            AttendingAdults = g.Status == RsvpStatus.Attending ? g.AttendingAdults : 1,
            AttendingChildren = g.Status == RsvpStatus.Attending ? g.AttendingChildren : 0,
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
        if (Input.Status == RsvpStatus.Attending)
        {
            if (Input.AttendingAdults < 1)
                ModelState.AddModelError("Input.AttendingAdults", ui.T("צריך לפחות מבוגר אחד", "At least 1 adult is needed"));
            if (Input.AttendingChildren < 0)
                ModelState.AddModelError("Input.AttendingChildren", ui.T("מספר הילדים לא יכול להיות שלילי", "Children can't be negative"));
            if (Input.AttendingAdults + Input.AttendingChildren > maxAttending)
                ModelState.AddModelError("Input.AttendingAdults", unlimited
                    ? ui.T($"מבוגרים וילדים יחד: עד {Guest.UnlimitedCap}", $"Adults and children together: at most {Guest.UnlimitedCap}")
                    : ui.T("מבוגרים וילדים יחד לא יכולים לעלות על מספר המוזמנים", "Adults and children together can't exceed the number invited"));
        }

        var phone = PhoneNormalizer.Normalize(Input.Phone);
        if (phone.IsValid && await db.Guests.AnyAsync(x => x.PhoneE164 == phone.E164 && x.Id != g.Id))
            ModelState.AddModelError("Input.Phone", ui.T("המספר כבר שייך לאורח אחר", "This number already belongs to another guest"));

        var people = Input.Status == RsvpStatus.Attending ? Input.AttendingAdults + Input.AttendingChildren : 0;
        if (!new DietaryAnswer(Input.DietVegetarian, Input.DietVegan, Input.DietGlutenFree, Input.DietNote).IsValid(people))
            ModelState.AddModelError("Input.DietVegetarian", ui.T($"מספרים בין 0 למספר המגיעים, והערה עד {Guest.DietNoteMax} תווים", $"Numbers from 0 up to the people attending, and a note of at most {Guest.DietNoteMax} characters"));

        if (!ModelState.IsValid) { await LoadFamiliesAsync(); return Page(); }

        g.Name = Input.Name.Trim();
        g.PhoneRaw = Input.Phone?.Trim() ?? "";
        g.PhoneE164 = phone.E164;
        g.PhoneError = phone.Error;
        g.InvitedCount = unlimited ? 0 : Input.InvitedCount;
        g.Notes = Input.Notes;
        g.DietVegetarian = Input.DietVegetarian; g.DietVegan = Input.DietVegan; g.DietGlutenFree = Input.DietGlutenFree;
        g.DietNote = string.IsNullOrWhiteSpace(Input.DietNote) ? null : Input.DietNote.Trim();

        // A manual status change is recorded as such, so the couple can tell phone-call
        // answers apart from guests who answered through the link.
        var attendingNow = Input.Status == RsvpStatus.Attending;
        var adults = attendingNow ? Input.AttendingAdults : 0;
        var children = attendingNow ? Input.AttendingChildren : 0;
        if (g.Status != Input.Status || (attendingNow && (g.AttendingAdults != adults || g.AttendingChildren != children)))
        {
            g.Status = Input.Status;
            g.AttendingCount = adults + children;
            g.AttendingChildren = children;
            g.Source = Input.Status == RsvpStatus.Pending ? RsvpSource.None : RsvpSource.Manual;
            g.RespondedAtUtc = Input.Status == RsvpStatus.Pending ? null : clock.UtcNow;
        }

        if (!await families.SetFamilyAsync(g, Input.FamilyId, Input.FamilyName, clock.UtcNow))
        {
            ModelState.AddModelError("Input.FamilyName", ui.T("נא להזין שם למשפחה החדשה", "Enter a name for the new family"));
            await LoadFamiliesAsync();
            return Page();
        }
        await db.SaveChangesAsync(); // a new family gets its id before the answer is shared
        await families.ShareAnswerAsync(g);
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

    private async Task LoadFamiliesAsync() => Families = await families.OptionsAsync();

    private async Task<bool> LoadAsync(int id)
    {
        Families = await families.OptionsAsync();
        Existing = await db.Guests.Include(x => x.Family).FirstOrDefaultAsync(x => x.Id == id);
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
