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

    public string VenueName { get; set; } = "";
    public string VenueAddress { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>File name inside {DataDir}/media, served at /media/invitation.</summary>
    public string? InvitationImageFile { get; set; }
    public DateTime? InvitationImageUpdatedAtUtc { get; set; }

    /// <summary>Alphanumeric sender ID shown to guests, if the SMS provider allows it.</summary>
    public string SmsSenderName { get; set; } = "";

    // Templates. Placeholders: {name} {couple} {link} {date} {venue}
    public string InviteTemplate { get; set; } =
        "{name}, שמחים להזמין אתכם לחתונה של {couple} ב-{date}! לפרטים ואישור הגעה: {link}";
    public string ReminderTemplate { get; set; } =
        "{name}, עוד לא אישרתם הגעה לחתונה של {couple}. נשמח לתשובה כאן: {link}";
    public string ThankYouTemplate { get; set; } =
        "{name}, תודה ששמחתם בשמחתנו! {couple}";

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
