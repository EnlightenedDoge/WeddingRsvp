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

    public static string TemplateEnFor(EventSettings s, MessageKind kind) => kind switch
    {
        MessageKind.Invite => s.InviteTemplateEn,
        MessageKind.Reminder => s.ReminderTemplateEn,
        MessageKind.ThankYou => s.ThankYouTemplateEn,
        _ => s.InviteTemplateEn,
    };

    /// <summary>The (template, isEnglish) pairs that make up one message for the couple's language mode.</summary>
    public static IReadOnlyList<(string Template, bool English)> ActiveTemplates(EventSettings s, MessageKind kind) =>
        s.LanguageMode switch
        {
            LanguageMode.EnglishOnly => [(TemplateEnFor(s, kind), true)],
            LanguageMode.Both => [(TemplateFor(s, kind), false), (TemplateEnFor(s, kind), true)],
            _ => [(TemplateFor(s, kind), false)],
        };

    /// <summary>
    /// The SMS text. In Both mode it carries the Hebrew and the English text one after the other, separated by an empty line
    /// (the single link opens a page with a language toggle).
    /// </summary>
    public static string Body(EventSettings s, MessageKind kind, string guestName, string link) =>
        string.Join("\n\n", ActiveTemplates(s, kind).Select(t => Render(t.Template, guestName, s, link, t.English)));

    public static string CoupleFor(EventSettings s, bool en) =>
        en && !string.IsNullOrWhiteSpace(s.CoupleNamesEn) ? s.CoupleNamesEn : s.CoupleNames;
    public static string VenueFor(EventSettings s, bool en) =>
        en && !string.IsNullOrWhiteSpace(s.VenueNameEn) ? s.VenueNameEn : s.VenueName;

    public static string Render(string template, string guestName, EventSettings s, string link, bool english = false) =>
        template
            .Replace("{name}", guestName)
            .Replace("{couple}", CoupleFor(s, english))
            .Replace("{link}", link)
            .Replace("{date}", s.StartsAtLocal?.ToString("dd/MM", CultureInfo.InvariantCulture) ?? "")
            .Replace("{venue}", VenueFor(s, english))
            .Trim();

    /// <summary>
    /// Event-level placeholders (the couple fills these in) that a template uses but that have no value yet.
    /// {name} comes from the guest list and {link} is generated per guest, so they are never "missing" here.
    /// </summary>
    public static IReadOnlyList<string> MissingFields(EventSettings s, string template, bool english = false)
    {
        var missing = new List<string>();
        void Check(string token, bool hasValue)
        {
            if (!hasValue && template.Contains(token, StringComparison.Ordinal)) missing.Add(token);
        }
        Check("{couple}", !string.IsNullOrWhiteSpace(CoupleFor(s, english)));
        Check("{date}", s.StartsAtLocal.HasValue);
        Check("{venue}", !string.IsNullOrWhiteSpace(VenueFor(s, english)));
        return missing;
    }

    /// <summary>Union over every template the couple's language mode will actually send.</summary>
    public static IReadOnlyList<string> MissingFields(EventSettings s, MessageKind kind) =>
        ActiveTemplates(s, kind).SelectMany(t => MissingFields(s, t.Template, t.English)).Distinct().ToList();

    public static string GuestLink(string publicBaseUrl, string token) =>
        publicBaseUrl.TrimEnd('/') + "/" + token;
}
