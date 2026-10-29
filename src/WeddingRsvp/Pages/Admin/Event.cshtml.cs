using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;

namespace WeddingRsvp.Pages.Admin;

/// <summary>A colour field: text box (the value that is saved), live chip / picker, default option, presets.</summary>
public sealed record ColorPicker(string Label, string Name, string? Value, string Default, string[] Presets);

public sealed record ImageSlot(string Kind, string Title, bool Has, long? Ticks, string? Lang);

public class EventModel(AppDbContext db, SettingsService settings, IsraelClock clock, IOptions<AppOptions> app, Ui ui, ISmsProvider sms, MapLinkResolver mapLinks) : PageModel
{
    private static readonly string[] AllowedImageTypes = [".jpg", ".jpeg", ".png", ".webp"];

    [BindProperty] public EventSettings Settings { get; set; } = new();
    [BindProperty] public IFormFile? Image { get; set; }

    public bool HasImage => Settings.InvitationImageFile is not null;
    public bool HasImageEn => Settings.InvitationImageFileEn is not null;
    public bool HasBackground => Settings.PageBackgroundImageFile is not null;
    public SegmentInfo InviteSegmentsEn { get; private set; }
    public SegmentInfo ReminderSegmentsEn { get; private set; }
    public SegmentInfo ThankYouSegmentsEn { get; private set; }
    /// <summary>What the invitation SMS looks like right now (empty fields shown as blanks), for the preview.</summary>
    public string PreviewBody { get; private set; } = "";
    public SegmentInfo PreviewSegments { get; private set; }
    public string PreviewReminder { get; private set; } = "";
    public SegmentInfo PreviewReminderSegments { get; private set; }
    public string PreviewThankYou { get; private set; } = "";
    public SegmentInfo PreviewThankYouSegments { get; private set; }
    /// <summary>Sample values for each placeholder, per language, so the page can count a template's rendered length while typing.</summary>
    /// <summary>How the active SMS provider bills a message (differs per provider and per encoding).</summary>
    public SmsCostRule CostRule(bool unicode) => sms.CostRule(unicode);
    /// <summary>The longest guest name on the list (worst case for the counters), or null while the list is empty.</summary>
    public string? LongestGuestName { get; private set; }
    public object SampleHe { get; private set; } = new();
    public object SampleEn { get; private set; } = new();
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
            ModelState.AddModelError("Settings.EndsAtLocal", ui.T("שעת הסיום צריכה להיות אחרי שעת ההתחלה", "The end must be after the start"));
        foreach (var (field, value) in new[] { ("PageBackgroundColor", Settings.PageBackgroundColor), ("PageAccentColor", Settings.PageAccentColor) })
            if (!string.IsNullOrWhiteSpace(value) && !HexColor.IsValid(value))
                ModelState.AddModelError("Settings." + field, ui.T("צבע חייב להיות בפורמט #RRGGBB", "A colour must look like #RRGGBB"));
        if (!string.IsNullOrWhiteSpace(Settings.WazeLink) && !MapLinks.IsWazeLink(Settings.WazeLink))
            ModelState.AddModelError("Settings.WazeLink", ui.T("הדביקו קישור Waze שמתחיל ב-https://waze.com", "Paste a Waze link that starts with https://waze.com"));
        if (!string.IsNullOrWhiteSpace(Settings.GoogleMapsLink) && !MapLinks.IsGoogleMapsLink(Settings.GoogleMapsLink))
            ModelState.AddModelError("Settings.GoogleMapsLink", ui.T("הדביקו קישור Google Maps (https://maps.app.goo.gl/… או https://www.google.com/maps/…)", "Paste a Google Maps link (https://maps.app.goo.gl/… or https://www.google.com/maps/…)"));
        if ((Settings.Latitude is null) != (Settings.Longitude is null))
            ModelState.AddModelError("Settings.Latitude", ui.T("יש למלא גם קו רוחב וגם קו אורך", "Fill in both latitude and longitude, or neither"));
        if (!ModelState.IsValid)
        {
            Describe(current);
            return Page();
        }

        // No coordinates typed in: read them from a pasted link, so one link drives both the Waze and the Google Maps button.
        var inferred = false;
        if (Settings.Latitude is null)
            foreach (var link in new[] { Settings.WazeLink, Settings.GoogleMapsLink })
                if (!string.IsNullOrWhiteSpace(link) && await mapLinks.ResolveAsync(link, HttpContext.RequestAborted) is { } c)
                {
                    Settings.Latitude = c.Lat;
                    Settings.Longitude = c.Lon;
                    inferred = true;
                    break;
                }

        current.CopyEditableFrom(Settings);
        await db.SaveChangesAsync();
        TempData["Flash"] = inferred
            ? ui.T("נשמר. מצאנו את המיקום בקישור, והוא ישמש גם ל-Waze וגם ל-Google Maps", "Saved. We found the location in the link and will use it for both Waze and Google Maps")
            : ui.T("נשמר", "Saved");
        return RedirectToPage();
    }

    /// <summary>kind: "he" or "en" (invitation images) or "bg" (guest page background photo).</summary>
    public async Task<IActionResult> OnPostImageAsync(string kind)
    {
        var current = await settings.GetAsync();
        var ext = Path.GetExtension(Image?.FileName ?? "").ToLowerInvariant();
        if (kind is not ("he" or "en" or "bg") || Image is null || Image.Length == 0
            || !AllowedImageTypes.Contains(ext) || Image.Length > 10_000_000)
        {
            TempData["Flash"] = ui.T("צריך קובץ תמונה (JPG/PNG/WEBP) עד 10MB", "An image file (JPG/PNG/WEBP) up to 10MB is required");
            return RedirectToPage();
        }

        var mediaDir = Path.Combine(Path.GetFullPath(app.Value.DataDir), "media", current.CoupleId.ToString());
        Directory.CreateDirectory(mediaDir);
        var stem = kind switch { "he" => "invitation-he", "en" => "invitation-en", _ => "background" };
        foreach (var old in Directory.GetFiles(mediaDir, stem + ".*")) System.IO.File.Delete(old);
        var fileName = stem + ext; // fixed names, never the client's file name
        await using (var fs = System.IO.File.Create(Path.Combine(mediaDir, fileName)))
            await Image.CopyToAsync(fs);

        var now = clock.UtcNow;
        switch (kind)
        {
            case "he": current.InvitationImageFile = fileName; current.InvitationImageUpdatedAtUtc = now; break;
            case "en": current.InvitationImageFileEn = fileName; current.InvitationImageEnUpdatedAtUtc = now; break;
            default: current.PageBackgroundImageFile = fileName; current.PageBackgroundImageUpdatedAtUtc = now; break;
        }
        await db.SaveChangesAsync();
        TempData["Flash"] = ui.T("התמונה עודכנה", "Image updated");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveImageAsync(string kind)
    {
        var current = await settings.GetAsync();
        var mediaDir = Path.Combine(Path.GetFullPath(app.Value.DataDir), "media", current.CoupleId.ToString());
        var file = kind switch
        {
            "he" => current.InvitationImageFile, "en" => current.InvitationImageFileEn,
            "bg" => current.PageBackgroundImageFile, _ => null,
        };
        if (file is not null)
        {
            var path = Path.Combine(mediaDir, Path.GetFileName(file));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            switch (kind)
            {
                case "he": current.InvitationImageFile = null; break;
                case "en": current.InvitationImageFileEn = null; break;
                default: current.PageBackgroundImageFile = null; break;
            }
            await db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    private void Describe(EventSettings s)
    {
        var sampleLink = MessageTemplates.GuestLink(app.Value.PublicBaseUrl, s.PreviewToken == "" ? "AbCdEfGhJk" : s.PreviewToken);
        // {name} is counted with the longest real guest name (worst case); a sample name until guests exist.
        LongestGuestName = db.Guests.AsNoTracking().Select(g => g.Name).AsEnumerable()
            .OrderByDescending(n => n.Length).FirstOrDefault();
        SegmentInfo Seg(string t) => SmsSegments.Count(MessageTemplates.Render(t, LongestGuestName ?? "משפחת ישראלי", s, sampleLink));
        InviteSegments = Seg(s.InviteTemplate);
        ReminderSegments = Seg(s.ReminderTemplate);
        ThankYouSegments = Seg(s.ThankYouTemplate);
        SegmentInfo SegEn(string t) => SmsSegments.Count(MessageTemplates.Render(t, LongestGuestName ?? "Cohen family", s, sampleLink, english: true));
        InviteSegmentsEn = SegEn(s.InviteTemplateEn);
        ReminderSegmentsEn = SegEn(s.ReminderTemplateEn);
        ThankYouSegmentsEn = SegEn(s.ThankYouTemplateEn);
        var previewName = s.LanguageMode == LanguageMode.EnglishOnly ? "Cohen family" : "משפחת ישראלי";
        PreviewBody = MessageTemplates.Body(s, MessageKind.Invite, previewName, sampleLink, placeholders: true);
        PreviewSegments = SmsSegments.Count(PreviewBody);
        PreviewReminder = MessageTemplates.Body(s, MessageKind.Reminder, previewName, sampleLink, placeholders: true);
        PreviewReminderSegments = SmsSegments.Count(PreviewReminder);
        PreviewThankYou = MessageTemplates.Body(s, MessageKind.ThankYou, previewName, sampleLink, placeholders: true);
        PreviewThankYouSegments = SmsSegments.Count(PreviewThankYou);
        object Sample(string name, bool en) => new
        {
            name,
            couple = MessageTemplates.CoupleFor(s, en),
            date = s.StartsAtLocal?.ToString("dd/MM", CultureInfo.InvariantCulture) ?? "",
            venue = MessageTemplates.VenueFor(s, en),
            link = sampleLink,
        };
        SampleHe = Sample(LongestGuestName ?? "משפחת ישראלי", false);
        SampleEn = Sample(LongestGuestName ?? "Cohen family", true);
    }
}
