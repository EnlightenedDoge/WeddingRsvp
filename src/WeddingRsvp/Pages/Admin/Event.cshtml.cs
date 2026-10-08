using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class EventModel(AppDbContext db, SettingsService settings, IsraelClock clock, IOptions<AppOptions> app) : PageModel
{
    private static readonly string[] AllowedImageTypes = [".jpg", ".jpeg", ".png", ".webp"];

    [BindProperty] public EventSettings Settings { get; set; } = new();
    [BindProperty] public IFormFile? Image { get; set; }

    public bool HasImage { get; private set; }
    public SegmentInfo InviteSegments { get; private set; }
    public SegmentInfo ReminderSegments { get; private set; }
    public SegmentInfo ThankYouSegments { get; private set; }

    public async Task OnGetAsync()
    {
        Settings = await settings.GetAsync();
        Describe(Settings);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var current = await settings.GetAsync();
        if (Settings.StartsAtLocal is { } from && Settings.EndsAtLocal is { } to && to <= from)
            ModelState.AddModelError("Settings.EndsAtLocal", "שעת הסיום צריכה להיות אחרי שעת ההתחלה");
        if (!ModelState.IsValid)
        {
            Describe(current);
            return Page();
        }

        current.CopyEditableFrom(Settings);
        await db.SaveChangesAsync();
        TempData["Flash"] = "נשמר";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImageAsync()
    {
        var current = await settings.GetAsync();
        var ext = Path.GetExtension(Image?.FileName ?? "").ToLowerInvariant();
        if (Image is null || Image.Length == 0 || !AllowedImageTypes.Contains(ext) || Image.Length > 10_000_000)
        {
            TempData["Flash"] = "צריך קובץ תמונה (JPG/PNG/WEBP) עד 10MB";
            return RedirectToPage();
        }

        var mediaDir = Path.Combine(Path.GetFullPath(app.Value.DataDir), "media");
        var fileName = "invitation" + ext;
        foreach (var old in Directory.GetFiles(mediaDir, "invitation.*")) System.IO.File.Delete(old);
        await using (var fs = System.IO.File.Create(Path.Combine(mediaDir, fileName)))
            await Image.CopyToAsync(fs);

        current.InvitationImageFile = fileName;
        current.InvitationImageUpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync();
        TempData["Flash"] = "התמונה עודכנה";
        return RedirectToPage();
    }

    private void Describe(EventSettings s)
    {
        HasImage = s.InvitationImageFile is not null;
        var sampleLink = MessageTemplates.GuestLink(app.Value.PublicBaseUrl, "AbCdEfGhJk");
        SegmentInfo Seg(string t) => SmsSegments.Count(MessageTemplates.Render(t, "משפחת ישראלי", s, sampleLink));
        InviteSegments = Seg(s.InviteTemplate);
        ReminderSegments = Seg(s.ReminderTemplate);
        ThankYouSegments = Seg(s.ThankYouTemplate);
    }
}
