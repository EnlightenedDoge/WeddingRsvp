using System.Globalization;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Pages;

/// <summary>Display strings shared by public and admin pages, in the request's UI language.</summary>
public static class Labels
{
    private static bool He => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "en";
    private static string T(string he, string en) => He ? he : en;

    public static string Status(RsvpStatus s) => s switch
    {
        RsvpStatus.Attending => T("מגיעים", "Attending"),
        RsvpStatus.Declined => T("לא מגיעים", "Declined"),
        _ => T("טרם ענו", "Awaiting"),
    };

    public static string StatusPill(RsvpStatus s) => s switch
    {
        RsvpStatus.Attending => "ok",
        RsvpStatus.Declined => "no",
        _ => "wait",
    };

    public static string Kind(MessageKind k) => k switch
    {
        MessageKind.Invite => T("הזמנה", "Invitation"),
        MessageKind.Reminder => T("תזכורת", "Reminder"),
        MessageKind.ThankYou => T("תודה", "Thank-you"),
        _ => T("בדיקה", "Test"),
    };

    public static string Delivery(MessageStatus s) => s switch
    {
        MessageStatus.Queued => T("בתור", "Queued"),
        MessageStatus.Sent => T("נשלח", "Sent"),
        MessageStatus.Delivered => T("התקבל", "Delivered"),
        MessageStatus.Failed => T("נכשל", "Failed"),
        _ => T("בוטל", "Cancelled"),
    };

    public static string DeliveryPill(MessageStatus s) => s switch
    {
        MessageStatus.Sent or MessageStatus.Delivered => "ok",
        MessageStatus.Failed => "no",
        MessageStatus.Queued => "wait",
        _ => "",
    };

    public static string Local(DateTime? utc) =>
        utc is { } u ? Services.IsraelClock.ToLocal(u).ToString("dd/MM HH:mm") : "—";

    /// <summary>Phone/import problems are stored in Hebrew; show the English equivalent when needed.</summary>
    public static string Problem(string? he) => he is null || He ? he ?? "" : he switch
    {
        "חסר מספר" => "Missing number",
        "מספר בינלאומי לא תקין" => "Invalid international number",
        "לא מספר נייד" => "Not a mobile number",
        "מספר לא תקין" => "Invalid number",
        "חסר שם" => "Missing name",
        "מספר כפול" => "Duplicate number",
        _ => he,
    };
}
