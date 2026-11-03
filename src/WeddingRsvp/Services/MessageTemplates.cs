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
    public static string Body(EventSettings s, MessageKind kind, string guestName, string link, bool placeholders = false) =>
        string.Join("\n\n", ActiveTemplates(s, kind).Select(t => Render(t.Template, guestName, s, link, t.English, placeholders)));

    public static string CoupleFor(EventSettings s, bool en) =>
        en && !string.IsNullOrWhiteSpace(s.CoupleNamesEn) ? s.CoupleNamesEn : s.CoupleNames;
    public static string VenueFor(EventSettings s, bool en) =>
        en && !string.IsNullOrWhiteSpace(s.VenueNameEn) ? s.VenueNameEn : s.VenueName;

    /// <param name="placeholders">For test messages and previews: a value the couple hasn't filled in stays visible as its
    /// placeholder ({couple}, {date}, {venue}) instead of leaving a gap. Real messages are never sent with a missing value (see MissingFields).</param>
    public static string Render(string template, string guestName, EventSettings s, string link, bool english = false, bool placeholders = false)
    {
        string Or(string value, string token) => placeholders && string.IsNullOrWhiteSpace(value) ? token : value;
        return template
            .Replace("{name}", guestName)
            .Replace("{couple}", Or(CoupleFor(s, english), "{couple}"))
            .Replace("{link}", link)
            .Replace("{date}", Or(s.StartsAtLocal?.ToString("dd/MM", CultureInfo.InvariantCulture) ?? "", "{date}"))
            .Replace("{venue}", Or(VenueFor(s, english), "{venue}"))
            .Trim();
    }

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

    /// <summary>The shared link of a group: /g/{code}.</summary>
    public static string GroupLink(string publicBaseUrl, string code) =>
        publicBaseUrl.TrimEnd('/') + "/g/" + code;

    /// <summary>The text a couple pastes into a group chat with the group link, in the couple's language mode.</summary>
    public static string GroupMessage(EventSettings s, string link)
    {
        string He() => $"שלום! אנחנו שמחים להזמין אתכם לחתונה של {s.CoupleNames}. לאישור הגעה, הזינו טלפון ושם בקישור: {link}";
        string En() => $"Hello! We are happy to invite you to the wedding of {(string.IsNullOrWhiteSpace(s.CoupleNamesEn) ? s.CoupleNames : s.CoupleNamesEn)}. To RSVP, enter your phone and name here: {link}";
        return s.LanguageMode switch
        {
            LanguageMode.HebrewOnly => He(),
            LanguageMode.EnglishOnly => En(),
            _ => He() + "\n\n" + En(),
        };
    }
}
