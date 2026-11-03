using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

/// <summary>
/// What every guest-facing page (the personal page and the group-link page) derives from the couple's event settings: language,
/// invitation image, couple-chosen colours, title and venue. Colours are validated hex before they reach CSS.
/// </summary>
public sealed class GuestPageLook
{
    public bool IsHe { get; private init; } = true;
    public bool ShowToggle { get; private init; }
    public string? ImageUrl { get; private init; }
    public string GoogleCalendarUrl { get; private init; } = "";
    public string? WazeUrl { get; private init; }
    public string? GoogleMapsUrl { get; private init; }
    public string Bg { get; private init; } = HexColor.DefaultBackground;
    public string Accent { get; private init; } = HexColor.DefaultAccent;
    public bool Dark { get; private init; }
    public string? BackgroundUrl { get; private init; }
    public string CoupleDisplay { get; private init; } = "";
    public string Title { get; private init; } = "";
    public string VenueDisplay { get; private init; } = "";

    /// <param name="lang">The ?lang= choice; only used when the couple offers both languages.</param>
    /// <param name="basePath">Where this page's media routes live (/{token}, /g/{code}, /admin/preview).</param>
    public static GuestPageLook Build(EventSettings ev, string? lang, string basePath)
    {
        var isHe = ev.LanguageMode switch
        {
            LanguageMode.HebrewOnly => true,
            LanguageMode.EnglishOnly => false,
            _ => (lang is "he" or "en" ? lang : ev.DefaultGuestLanguage) != "en",
        };
        var en = !isHe;
        var hasHe = ev.InvitationImageFile is not null;
        var hasEn = ev.InvitationImageFileEn is not null;
        var ticks = (en && hasEn ? ev.InvitationImageEnUpdatedAtUtc : hasHe ? ev.InvitationImageUpdatedAtUtc : ev.InvitationImageEnUpdatedAtUtc)?.Ticks;

        var bg = HexColor.OrNull(ev.PageBackgroundColor) ?? HexColor.DefaultBackground;
        var accent = HexColor.OrNull(ev.PageAccentColor) ?? HexColor.DefaultAccent;
        var bgUrl = ev.PageBackgroundImageFile is null ? null : $"{basePath}/background?v={ev.PageBackgroundImageUpdatedAtUtc?.Ticks}";

        var couple = en && !string.IsNullOrWhiteSpace(ev.CoupleNamesEn) ? ev.CoupleNamesEn : ev.CoupleNames;
        var title = !string.IsNullOrWhiteSpace(ev.Title) ? ev.Title
            : string.IsNullOrWhiteSpace(couple) ? ""
            : isHe ? $"החתונה של {couple}" : $"The wedding of {couple}";
        var venue = en && !string.IsNullOrWhiteSpace(ev.VenueNameEn) ? ev.VenueNameEn : ev.VenueName;
        var addr = en && !string.IsNullOrWhiteSpace(ev.VenueAddressEn) ? ev.VenueAddressEn : ev.VenueAddress;

        return new GuestPageLook
        {
            IsHe = isHe,
            ShowToggle = ev.LanguageMode == LanguageMode.Both,
            ImageUrl = hasHe || hasEn ? $"{basePath}/invitation?lang={(en ? "en" : "he")}&v={ticks}" : null,
            GoogleCalendarUrl = CalendarLinks.GoogleCalendarUrl(ev) ?? "",
            WazeUrl = CalendarLinks.WazeUrl(ev),
            GoogleMapsUrl = CalendarLinks.GoogleMapsUrl(ev),
            Bg = bg, Accent = accent, BackgroundUrl = bgUrl,
            Dark = bgUrl is not null || HexColor.IsDark(bg),
            CoupleDisplay = couple, Title = title,
            VenueDisplay = string.Join(", ", new[] { venue, addr }.Where(x => !string.IsNullOrWhiteSpace(x))),
        };
    }
}
