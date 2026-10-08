using System.Globalization;
using System.Text;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public static class CalendarLinks
{
    private const string UtcStamp = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Waze deep link. Coordinates beat addresses: venue names often resolve to the wrong place.</summary>
    public static string? WazeUrl(EventSettings s)
    {
        if (s.Latitude is { } lat && s.Longitude is { } lon)
            return string.Create(CultureInfo.InvariantCulture,
                $"https://waze.com/ul?ll={lat:0.######},{lon:0.######}&navigate=yes");
        if (!string.IsNullOrWhiteSpace(s.VenueDisplay))
            return $"https://waze.com/ul?q={Uri.EscapeDataString(s.VenueDisplay)}&navigate=yes";
        return null;
    }

    /// <summary>End time, defaulting to four hours after the start when the couple left it empty.</summary>
    private static DateTime EndOf(EventSettings s, DateTime start) =>
        s.EndsAtLocal is { } e && e > start ? e : start.AddHours(4);

    public static string? GoogleCalendarUrl(EventSettings s)
    {
        if (s.StartsAtLocal is not { } startLocal) return null;
        var start = IsraelClock.ToUtc(startLocal).ToString(UtcStamp, CultureInfo.InvariantCulture);
        var end = IsraelClock.ToUtc(EndOf(s, startLocal)).ToString(UtcStamp, CultureInfo.InvariantCulture);
        return "https://calendar.google.com/calendar/render?action=TEMPLATE" +
               $"&text={Uri.EscapeDataString(s.Title)}" +
               $"&dates={start}/{end}" +
               $"&location={Uri.EscapeDataString(s.VenueDisplay)}" +
               "&ctz=Asia/Jerusalem";
    }

    /// <summary>Null when the event has no start date yet.</summary>
    public static string? BuildIcs(EventSettings s, string publicBaseUrl, DateTime utcNow)
    {
        if (s.StartsAtLocal is not { } startLocal) return null;
        var host = Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var u) ? u.Host : "localhost";
        var sb = new StringBuilder();
        void Line(string l) { sb.Append(Fold(l)).Append("\r\n"); }

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//WeddingRsvp//HE");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("BEGIN:VEVENT");
        Line($"UID:wedding@{host}");
        Line($"DTSTAMP:{utcNow.ToString(UtcStamp, CultureInfo.InvariantCulture)}");
        Line($"DTSTART:{IsraelClock.ToUtc(startLocal).ToString(UtcStamp, CultureInfo.InvariantCulture)}");
        Line($"DTEND:{IsraelClock.ToUtc(EndOf(s, startLocal)).ToString(UtcStamp, CultureInfo.InvariantCulture)}");
        Line($"SUMMARY:{Escape(s.Title)}");
        if (!string.IsNullOrWhiteSpace(s.VenueDisplay)) Line($"LOCATION:{Escape(s.VenueDisplay)}");
        if (WazeUrl(s) is { } waze) Line($"DESCRIPTION:{Escape("ניווט: " + waze)}");
        Line("END:VEVENT");
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static string Escape(string v) =>
        v.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    /// <summary>RFC 5545 folding: lines max 75 octets (Hebrew is 2 bytes/char in UTF-8).</summary>
    private static string Fold(string line)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var c in line)
        {
            var n = Encoding.UTF8.GetByteCount(new[] { c });
            if (bytes + n > 73) { sb.Append("\r\n "); bytes = 1; }
            sb.Append(c);
            bytes += n;
        }
        return sb.ToString();
    }
}
