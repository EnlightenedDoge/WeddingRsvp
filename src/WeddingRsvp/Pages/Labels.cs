using WeddingRsvp.Domain;

namespace WeddingRsvp.Pages;

/// <summary>Hebrew display strings shared by public and admin pages.</summary>
public static class Labels
{
    public static string Status(RsvpStatus s) => s switch
    {
        RsvpStatus.Attending => "מגיעים",
        RsvpStatus.Declined => "לא מגיעים",
        _ => "טרם ענו",
    };

    public static string Kind(MessageKind k) => k switch
    {
        MessageKind.Invite => "הזמנה",
        MessageKind.Reminder => "תזכורת",
        MessageKind.ThankYou => "תודה",
        _ => "בדיקה",
    };

    public static string Delivery(Domain.MessageStatus s) => s switch
    {
        Domain.MessageStatus.Queued => "בתור",
        Domain.MessageStatus.Sent => "נשלח",
        Domain.MessageStatus.Delivered => "התקבל",
        Domain.MessageStatus.Failed => "נכשל",
        _ => "בוטל",
    };

    public static string Local(DateTime? utc) =>
        utc is { } u ? IsraelClockFormat(u) : "—";

    private static string IsraelClockFormat(DateTime utc) =>
        Services.IsraelClock.ToLocal(utc).ToString("dd/MM HH:mm");
}
