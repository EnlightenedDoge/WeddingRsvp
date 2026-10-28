using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public class RsvpModel(RsvpService rsvp, SettingsService settings, ILogger<RsvpModel> log) : PageModel
{
    public Guest Guest { get; private set; } = null!;
    public EventSettings Event { get; private set; } = null!;
    public bool IsClosed { get; private set; }
    public bool JustSaved { get; private set; }
    public string? Error { get; private set; }

    public string? ImageUrl { get; private set; }
    public string GoogleCalendarUrl { get; private set; } = "";
    public string? WazeUrl { get; private set; }

    /// <summary>Language of this page view: fixed by the couple's mode, or chosen with ?lang= when both are offered.</summary>
    [BindProperty(SupportsGet = true)] public string? Lang { get; set; }
    public bool IsHe { get; private set; } = true;
    public bool ShowToggle { get; private set; }
    public string T(string he, string en) => IsHe ? he : en;
    public CultureInfo Culture => IsHe ? new CultureInfo("he-IL") : new CultureInfo("en-US");

    // Couple-chosen look (colours are validated hex before they get anywhere near CSS).
    public string Bg { get; private set; } = HexColor.DefaultBackground;
    public string Accent { get; private set; } = HexColor.DefaultAccent;
    public bool Dark { get; private set; }
    public string? BackgroundUrl { get; private set; }

    public string Title { get; private set; } = "";
    public string CoupleDisplay { get; private set; } = "";
    public string VenueDisplay { get; private set; } = "";

    [BindProperty] public bool? Attending { get; set; }
    /// <summary>Adults and children attending. Default on a first visit: 1 adult, 0 children.</summary>
    [BindProperty] public int? Adults { get; set; }
    [BindProperty] public int Children { get; set; }
    /// <summary>Old single-number form field; only read when <see cref="Adults"/> is missing (a page opened before the update).</summary>
    [BindProperty] public int Count { get; set; }

    /// <summary>"2 adults, 1 child" in the page language (children left out when there are none).</summary>
    public string People(int adults, int children)
    {
        var text = IsHe ? $"{adults} {(adults == 1 ? "מבוגר" : "מבוגרים")}" : $"{adults} adult{(adults == 1 ? "" : "s")}";
        if (children <= 0) return text;
        return text + (IsHe ? $", {children} {(children == 1 ? "ילד" : "ילדים")}" : $", {children} child{(children == 1 ? "" : "ren")}");
    }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        var g = await rsvp.FindAsync(token);
        if (g is null) return NotFound();
        // Loading the page is not an "open" (scanners and previews do it); the page reports back once a person sees it.
        log.LogInformation("Guest page {Method} for guest {GuestId} from {Ip}, user agent '{UserAgent}'",
            Request.Method, g.Id, HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString());
        await LoadAsync(g);

        Attending = g.Status switch
        {
            RsvpStatus.Attending => true,
            RsvpStatus.Declined => false,
            _ => null,
        };
        var attendingNow = g.Status == RsvpStatus.Attending;
        Adults = attendingNow ? g.AttendingAdults : 1;
        Children = attendingNow ? g.AttendingChildren : 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token)
    {
        Adults ??= Count > 0 ? Count : 1;
        var result = await rsvp.SubmitAsync(token, Attending, Adults.Value, Children);
        if (result == RsvpResult.NotFound) return NotFound();

        var g = (await rsvp.FindAsync(token))!;
        await LoadAsync(g);

        switch (result)
        {
            case RsvpResult.Saved:
                JustSaved = true;
                break;
            case RsvpResult.MissingAnswer:
                Error = T("נא לבחור אם מגיעים כדי להמשיך.", "Please choose whether you are attending to continue.");
                break;
            case RsvpResult.InvalidCount:
                Error = T($"יש לבחור לפחות מבוגר אחד, ובסך הכול עד {g.MaxHeadcount} אורחים (מבוגרים וילדים).", $"Choose at least 1 adult, and no more than {g.MaxHeadcount} guests in total (adults and children).");
                break;
            case RsvpResult.Closed:
                Error = T("האירוע כבר התחיל, אי אפשר לעדכן תשובה.", "The event has started, so the answer can no longer be changed.");
                break;
        }
        return Page();
    }

    private async Task LoadAsync(Guest g)
    {
        Guest = g;
        Event = await settings.GetAsync();
        IsClosed = await rsvp.IsClosedAsync();

        ShowToggle = Event.LanguageMode == LanguageMode.Both;
        IsHe = Event.LanguageMode switch
        {
            LanguageMode.HebrewOnly => true,
            LanguageMode.EnglishOnly => false,
            _ => (Lang is "he" or "en" ? Lang : Event.DefaultGuestLanguage) != "en",
        };

        var en = !IsHe;
        var hasHe = Event.InvitationImageFile is not null;
        var hasEn = Event.InvitationImageFileEn is not null;
        var ticks = (en && hasEn ? Event.InvitationImageEnUpdatedAtUtc : hasHe ? Event.InvitationImageUpdatedAtUtc : Event.InvitationImageEnUpdatedAtUtc)?.Ticks;
        ImageUrl = hasHe || hasEn ? $"/{g.Token}/invitation?lang={(en ? "en" : "he")}&v={ticks}" : null;

        GoogleCalendarUrl = CalendarLinks.GoogleCalendarUrl(Event) ?? "";
        WazeUrl = CalendarLinks.WazeUrl(Event);

        Bg = HexColor.OrNull(Event.PageBackgroundColor) ?? Bg;
        Accent = HexColor.OrNull(Event.PageAccentColor) ?? Accent;
        BackgroundUrl = Event.PageBackgroundImageFile is null ? null : $"/{g.Token}/background?v={Event.PageBackgroundImageUpdatedAtUtc?.Ticks}";
        Dark = BackgroundUrl is not null || HexColor.IsDark(Bg);

        CoupleDisplay = en && !string.IsNullOrWhiteSpace(Event.CoupleNamesEn) ? Event.CoupleNamesEn : Event.CoupleNames;
        Title = !string.IsNullOrWhiteSpace(Event.Title) ? Event.Title
            : string.IsNullOrWhiteSpace(CoupleDisplay) ? ""
            : T($"החתונה של {CoupleDisplay}", $"The wedding of {CoupleDisplay}");
        var venue = en && !string.IsNullOrWhiteSpace(Event.VenueNameEn) ? Event.VenueNameEn : Event.VenueName;
        var addr = en && !string.IsNullOrWhiteSpace(Event.VenueAddressEn) ? Event.VenueAddressEn : Event.VenueAddress;
        VenueDisplay = string.Join(", ", new[] { venue, addr }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
