using System.Globalization;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public static class MessageTemplates
{
    public const string Placeholders = "{name} {couple} {link} {date} {venue}";

    public static string TemplateFor(EventSettings s, MessageKind kind) => kind switch
    {
        MessageKind.Invite => s.InviteTemplate,
        MessageKind.Reminder => s.ReminderTemplate,
        MessageKind.ThankYou => s.ThankYouTemplate,
        _ => s.InviteTemplate,
    };

    public static string Render(string template, string guestName, EventSettings s, string link) =>
        template
            .Replace("{name}", guestName)
            .Replace("{couple}", s.CoupleNames)
            .Replace("{link}", link)
            .Replace("{date}", s.StartsAtLocal?.ToString("dd/MM", CultureInfo.InvariantCulture) ?? "")
            .Replace("{venue}", s.VenueName)
            .Trim();

    /// <summary>
    /// Event-level placeholders (the couple fills these in) that a template uses but that have no value yet.
    /// {name} comes from the guest list and {link} is generated per guest, so they are never "missing" here.
    /// </summary>
    public static IReadOnlyList<string> MissingFields(EventSettings s, string template)
    {
        var missing = new List<string>();
        void Check(string token, bool hasValue)
        {
            if (!hasValue && template.Contains(token, StringComparison.Ordinal)) missing.Add(token);
        }
        Check("{couple}", !string.IsNullOrWhiteSpace(s.CoupleNames));
        Check("{date}", s.StartsAtLocal.HasValue);
        Check("{venue}", !string.IsNullOrWhiteSpace(s.VenueName));
        return missing;
    }

    public static IReadOnlyList<string> MissingFields(EventSettings s, MessageKind kind) =>
        MissingFields(s, TemplateFor(s, kind));

    public static string GuestLink(string publicBaseUrl, string token) =>
        publicBaseUrl.TrimEnd('/') + "/" + token;
}
