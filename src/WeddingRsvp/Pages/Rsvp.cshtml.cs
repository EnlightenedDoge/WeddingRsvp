using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public class RsvpModel(RsvpService rsvp, SettingsService settings, ILogger<RsvpModel> log, IAuthorizationService authz) : PageModel
{
    /// <summary>The couple's own preview (/admin/preview): same page, nothing stored, no open report.</summary>
    private bool _demoLink;
    public bool IsPreview => _demoLink || Request.Path.StartsWithSegments("/admin/preview");
    /// <summary>URL prefix of this page's own sub-routes (invitation image, background, calendar file, language links).</summary>
    public string BasePath => IsPreview ? "/admin/preview" : "/" + Guest.Token;

    public Guest Guest { get; protected set; } = null!;
    public EventSettings Event { get; protected set; } = null!;
    public bool IsClosed { get; protected set; }
    public bool JustSaved { get; protected set; }
    public string? Error { get; protected set; }

    public string? ImageUrl { get; private set; }
    public string GoogleCalendarUrl { get; private set; } = "";
    public string? WazeUrl { get; private set; }
    public string? GoogleMapsUrl { get; private set; }

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
    /// <summary>Dietary answer (only used while the couple has turned the question on).</summary>
    [BindProperty] public int DietVegetarian { get; set; }
    [BindProperty] public int DietVegan { get; set; }
    [BindProperty] public int DietGlutenFree { get; set; }
    [BindProperty] public string? DietNote { get; set; }
    private DietaryAnswer DietAnswer() => new(DietVegetarian, DietVegan, DietGlutenFree, DietNote);
    private string DietError => T($"בבקשה לבחור מספרים בין 0 למספר האורחים, והערה עד {Guest.DietNoteMax} תווים.", $"Please use numbers from 0 up to the number of guests, and a note of at most {Guest.DietNoteMax} characters.");

    /// <summary>Old single-number form field; only read when <see cref="Adults"/> is missing (a page opened before the update).</summary>
    [BindProperty] public int Count { get; set; }

    /// <summary>"2 adults, 1 child" in the page language (children left out when there are none).</summary>
    public string People(int adults, int children)
    {
        var text = IsHe ? $"{adults} {(adults == 1 ? "מבוגר" : "מבוגרים")}" : $"{adults} adult{(adults == 1 ? "" : "s")}";
        if (children <= 0) return text;
        return text + (IsHe ? $", {children} {(children == 1 ? "ילד" : "ילדים")}" : $", {children} child{(children == 1 ? "" : "ren")}");
    }

    public async Task<IActionResult> OnGetAsync(string? token)
    {
        if (IsPreview)
        {
            if (!(await authz.AuthorizeAsync(User, "Couple")).Succeeded) return Challenge();
            await LoadAsync(SampleGuest());
            Guest.Name = IsHe ? "משפחת ישראלי" : "Cohen family";
            return Page();
        }
        var g = await rsvp.FindAsync(token);
        if (g is null) return NotFound();
        if (g.IsDemo) // the demo link from a test message: same as the couple's preview, but no sign-in
        {
            _demoLink = true;
            await LoadAsync(g);
            g.Name = IsHe ? "משפחת ישראלי" : "Cohen family";
            return Page();
        }
        // Loading the page is not an "open" (scanners and previews do it); the page reports back once a person sees it.
        // The user agent is attacker-supplied: strip control characters (log-injection) and cap its length.
        var ua = Request.Headers.UserAgent.ToString();
        ua = new string(ua.Where(c => !char.IsControl(c)).Take(200).ToArray());
        log.LogInformation("Guest page {Method} for guest {GuestId} from {Ip}, user agent '{UserAgent}'",
            Request.Method, g.Id, HttpContext.Connection.RemoteIpAddress, ua);
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
        DietVegetarian = g.DietVegetarian; DietVegan = g.DietVegan; DietGlutenFree = g.DietGlutenFree; DietNote = g.DietNote;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? token)
    {
        if (IsPreview) return await PreviewPostAsync();
        Adults ??= Count > 0 ? Count : 1;
        if (!IsPreview && await rsvp.FindAsync(token) is { IsDemo: true } demo) { _demoLink = true; return await PreviewPostAsync(demo); }
        var result = await rsvp.SubmitAsync(token, Attending, Adults.Value, Children, DietAnswer());
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
            case RsvpResult.InvalidDiet:
                Error = DietError;
                break;
            case RsvpResult.Closed:
                Error = T("האירוע כבר התחיל, אי אפשר לעדכן תשובה.", "The event has started, so the answer can no longer be changed.");
                break;
        }
        return Page();
    }

    // The couple's preview: a made-up guest that is never saved. An answer is checked like a real one and shown back, then forgotten.
    private static Guest SampleGuest() => new() { Name = "", Token = "PREVIEW000", InvitedCount = 4, Status = RsvpStatus.Pending };

    private async Task<IActionResult> PreviewPostAsync(Guest? demo = null)
    {
        // The couple's own /admin/preview needs their session; the demo link from a test message is open like any guest link.
        if (Request.Path.StartsWithSegments("/admin/preview") && !(await authz.AuthorizeAsync(User, "Couple")).Succeeded) return Challenge();
        var g = demo ?? SampleGuest();
        await LoadAsync(g);
        g.Name = IsHe ? "משפחת ישראלי" : "Cohen family";
        Adults ??= Count > 0 ? Count : 1;
        if (Attending is null)
            Error = T("נא לבחור אם מגיעים כדי להמשיך.", "Please choose whether you are attending to continue.");
        else if (Attending == true && (Adults < 1 || Children < 0 || (long)Adults.Value + Children > g.MaxHeadcount))
            Error = T($"יש לבחור לפחות מבוגר אחד, ובסך הכול עד {g.MaxHeadcount} אורחים (מבוגרים וילדים).", $"Choose at least 1 adult, and no more than {g.MaxHeadcount} guests in total (adults and children).");
        else if (Event.AskDietary && Attending == true && !DietAnswer().IsValid(Adults.Value + Children))
            Error = DietError;
        else
        {
            g.Status = Attending.Value ? RsvpStatus.Attending : RsvpStatus.Declined;
            if (Event.AskDietary)
            {
                g.DietVegetarian = Attending == true ? DietVegetarian : 0; g.DietVegan = Attending == true ? DietVegan : 0;
                g.DietGlutenFree = Attending == true ? DietGlutenFree : 0; g.DietNote = Attending == true ? DietNote?.Trim() : null;
            }
            g.AttendingCount = Attending.Value ? Adults.Value + Children : 0;
            g.AttendingChildren = Attending.Value ? Children : 0;
            JustSaved = true;
        }
        return Page();
    }

    protected async Task LoadAsync(Guest g)
    {
        Guest = g;
        Event = await settings.GetAsync();
        IsClosed = !IsPreview && await rsvp.IsClosedAsync();

        var look = GuestPageLook.Build(Event, Lang, BasePath);
        ShowToggle = look.ShowToggle; IsHe = look.IsHe; ImageUrl = look.ImageUrl;
        GoogleCalendarUrl = look.GoogleCalendarUrl; WazeUrl = look.WazeUrl; GoogleMapsUrl = look.GoogleMapsUrl;
        Bg = look.Bg; Accent = look.Accent; BackgroundUrl = look.BackgroundUrl; Dark = look.Dark;
        CoupleDisplay = look.CoupleDisplay; Title = look.Title; VenueDisplay = look.VenueDisplay;
    }
}
