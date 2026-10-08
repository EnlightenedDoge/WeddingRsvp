namespace WeddingRsvp.Domain;

/// <summary>
/// One row per couple holding everything about the event and the campaign.
/// *Local times are Israel local time (Asia/Jerusalem), entered by the admin as-is.
/// </summary>
public class EventSettings : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }

    // Nothing about the couple is assumed: these stay empty until the couple (or the site admin at
    // account creation) fills them in. MessageTemplates.MissingFields() blocks sending until they are set.
    public string CoupleNames { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime? StartsAtLocal { get; set; }
    public DateTime? EndsAtLocal { get; set; }

    /// <summary>Optional Latin-script versions for English messages and the English page; fall back to the Hebrew ones.</summary>
    public string CoupleNamesEn { get; set; } = "";
    public string VenueNameEn { get; set; } = "";
    public string VenueAddressEn { get; set; } = "";

    public string VenueName { get; set; } = "";
    public string VenueAddress { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public LanguageMode LanguageMode { get; set; } = LanguageMode.HebrewOnly;
    /// <summary>"he" or "en": the language the guest page opens in when LanguageMode is Both.</summary>
    public string DefaultGuestLanguage { get; set; } = "he";

    /// <summary>Hebrew invitation image: file name inside {DataDir}/media/{coupleId}/.</summary>
    public string? InvitationImageFile { get; set; }
    public DateTime? InvitationImageUpdatedAtUtc { get; set; }
    /// <summary>English invitation image.</summary>
    public string? InvitationImageFileEn { get; set; }
    public DateTime? InvitationImageEnUpdatedAtUtc { get; set; }

    // Guest page look, chosen by the couple. Colours are validated as #RRGGBB before they reach CSS.
    public string? PageBackgroundColor { get; set; }
    public string? PageAccentColor { get; set; }
    public string? PageBackgroundImageFile { get; set; }
    public DateTime? PageBackgroundImageUpdatedAtUtc { get; set; }

    /// <summary>Alphanumeric sender ID shown to guests, if the SMS provider allows it.</summary>
    public string SmsSenderName { get; set; } = "";

    // Templates. Placeholders: {name} {couple} {link} {date} {venue}
    public string InviteTemplate { get; set; } =
        "{name}, שמחים להזמין אתכם לחתונה של {couple} ב-{date}! לפרטים ואישור הגעה: {link}";
    public string ReminderTemplate { get; set; } =
        "{name}, עוד לא אישרתם הגעה לחתונה של {couple}. נשמח לתשובה כאן: {link}";
    public string ThankYouTemplate { get; set; } =
        "{name}, תודה ששמחתם בשמחתנו! {couple}";
    public string InviteTemplateEn { get; set; } =
        "{name}, we'd love you to join the wedding of {couple} on {date}! Details and RSVP: {link}";
    public string ReminderTemplateEn { get; set; } =
        "{name}, we haven't heard from you yet about the wedding of {couple}. Please reply here: {link}";
    public string ThankYouTemplateEn { get; set; } =
        "{name}, thank you for celebrating with us! {couple}";

    /// <summary>When set, invitations go out automatically once this local time passes.</summary>
    public DateTime? AutoInviteAtLocal { get; set; }

    public bool RemindersEnabled { get; set; } = true;
    public int ReminderIntervalDays { get; set; } = 4;
    public int MaxReminders { get; set; } = 2;

    public bool ThankYouEnabled { get; set; } = true;
    public ThankYouAudience ThankYouAudience { get; set; } = ThankYouAudience.AttendingOnly;
    /// <summary>Hour (local) on the day after the event to send thank-you messages.</summary>
    public int ThankYouHourLocal { get; set; } = 12;

    /// <summary>Kill switch: when true the dispatcher sends nothing (queue keeps growing).</summary>
    public bool SendingPaused { get; set; }

    public bool HasStartDate => StartsAtLocal.HasValue;

    public string VenueDisplay =>
        string.Join(", ", new[] { VenueName, VenueAddress }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public void CopyEditableFrom(EventSettings s)
    {
        CoupleNames = s.CoupleNames?.Trim() ?? "";
        CoupleNamesEn = s.CoupleNamesEn?.Trim() ?? "";
        VenueNameEn = s.VenueNameEn?.Trim() ?? "";
        VenueAddressEn = s.VenueAddressEn?.Trim() ?? "";
        LanguageMode = s.LanguageMode;
        DefaultGuestLanguage = s.DefaultGuestLanguage == "en" ? "en" : "he";
        PageBackgroundColor = HexColor.OrNull(s.PageBackgroundColor);
        PageAccentColor = HexColor.OrNull(s.PageAccentColor);
        InviteTemplateEn = s.InviteTemplateEn?.Trim() ?? "";
        ReminderTemplateEn = s.ReminderTemplateEn?.Trim() ?? "";
        ThankYouTemplateEn = s.ThankYouTemplateEn?.Trim() ?? "";
        Title = s.Title?.Trim() ?? "";
        StartsAtLocal = s.StartsAtLocal;
        EndsAtLocal = s.EndsAtLocal;
        VenueName = s.VenueName?.Trim() ?? "";
        VenueAddress = s.VenueAddress?.Trim() ?? "";
        Latitude = s.Latitude;
        Longitude = s.Longitude;
        SmsSenderName = s.SmsSenderName?.Trim() ?? "";
        InviteTemplate = s.InviteTemplate?.Trim() ?? "";
        ReminderTemplate = s.ReminderTemplate?.Trim() ?? "";
        ThankYouTemplate = s.ThankYouTemplate?.Trim() ?? "";
        AutoInviteAtLocal = s.AutoInviteAtLocal;
        RemindersEnabled = s.RemindersEnabled;
        ReminderIntervalDays = Math.Max(1, s.ReminderIntervalDays);
        MaxReminders = Math.Max(0, s.MaxReminders);
        ThankYouEnabled = s.ThankYouEnabled;
        ThankYouAudience = s.ThankYouAudience;
        ThankYouHourLocal = Math.Clamp(s.ThankYouHourLocal, 0, 23);
    }
}

/// <summary>Couple-chosen colours are the only user input that ends up inside CSS, so only exact #RRGGBB passes.</summary>
public static class HexColor
{
    public static bool IsValid(string? v) =>
        v is { Length: 7 } && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    public static string? OrNull(string? v) => IsValid(v) ? v!.ToUpperInvariant() : null;

    /// <summary>Relative luminance below ~0.18: light text is needed on top.</summary>
    public static bool IsDark(string hex)
    {
        static double F(int c8) { var c = c8 / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var n = Convert.ToInt32(hex.Substring(1), 16);
        return 0.2126 * F(n >> 16) + 0.7152 * F((n >> 8) & 255) + 0.0722 * F(n & 255) < 0.18;
    }

    /// <summary>True for colours that need dark text on top (perceived luminance above ~0.6).</summary>
    public static bool IsLight(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return (0.299 * r + 0.587 * g + 0.114 * b) / 255 > 0.6;
    }
}
