using System.Text;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

/// <summary>The guest list as a CSV (UTF-8 with a BOM so Excel opens Hebrew correctly). The family is the Family entity; load it with the guests.</summary>
public static class GuestCsv
{
    public static byte[] Build(IEnumerable<Guest> guests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("שם,משפחה,מזהה משפחה,טלפון,מוזמנים,סטטוס,מגיעים,מבוגרים,ילדים,צמחוניים,טבעוניים,ללא גלוטן,הערות תזונה,הערות");
        foreach (var g in guests)
        {
            var status = g.Status switch
            {
                RsvpStatus.Attending => "מגיעים",
                RsvpStatus.Declined => "לא מגיעים",
                _ => "טרם ענו",
            };
            sb.AppendLine(string.Join(",",
                CsvReader.EscapeText(g.Name), CsvReader.EscapeText(g.Family?.Name), CsvReader.EscapeText(g.Family is { } fm ? fm.ExternalId ?? "F" + fm.Id : null),
                CsvReader.Escape(g.PhoneRaw), g.NoGuestLimit ? CsvReader.Escape("ללא הגבלה") : g.InvitedCount.ToString(),
                status, g.AttendingCount, g.AttendingAdults, g.AttendingChildren, g.DietVegetarian, g.DietVegan, g.DietGlutenFree,
                CsvReader.EscapeText(g.DietNote), CsvReader.EscapeText(g.Notes)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}
