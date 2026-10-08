using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public class RsvpModel(RsvpService rsvp, SettingsService settings) : PageModel
{
    public Guest Guest { get; private set; } = null!;
    public EventSettings Event { get; private set; } = null!;
    public bool IsClosed { get; private set; }
    public bool JustSaved { get; private set; }
    public string? Error { get; private set; }

    public string? ImageUrl { get; private set; }
    public string GoogleCalendarUrl { get; private set; } = "";
    public string? WazeUrl { get; private set; }

    [BindProperty] public bool? Attending { get; set; }
    [BindProperty] public int Count { get; set; }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        var g = await rsvp.OpenAsync(token);
        if (g is null) return NotFound();
        await LoadAsync(g);

        Attending = g.Status switch
        {
            RsvpStatus.Attending => true,
            RsvpStatus.Declined => false,
            _ => null,
        };
        Count = g.Status == RsvpStatus.Attending ? g.AttendingCount : g.InvitedCount;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token)
    {
        var result = await rsvp.SubmitAsync(token, Attending, Count);
        if (result == RsvpResult.NotFound) return NotFound();

        var g = (await rsvp.FindAsync(token))!;
        await LoadAsync(g);

        switch (result)
        {
            case RsvpResult.Saved:
                JustSaved = true;
                break;
            case RsvpResult.MissingAnswer:
                Error = "בחרו אם אתם מגיעים.";
                break;
            case RsvpResult.InvalidCount:
                Error = $"אפשר לאשר בין 1 ל-{g.InvitedCount} אורחים.";
                break;
            case RsvpResult.Closed:
                Error = "האירוע כבר התחיל, אי אפשר לעדכן תשובה.";
                break;
        }
        return Page();
    }

    private async Task LoadAsync(Guest g)
    {
        Guest = g;
        Event = await settings.GetAsync();
        IsClosed = await rsvp.IsClosedAsync();
        ImageUrl = Event.InvitationImageFile is null
            ? null
            : $"/media/invitation?v={Event.InvitationImageUpdatedAtUtc?.Ticks}";
        GoogleCalendarUrl = CalendarLinks.GoogleCalendarUrl(Event) ?? "";
        WazeUrl = CalendarLinks.WazeUrl(Event);
    }
}
