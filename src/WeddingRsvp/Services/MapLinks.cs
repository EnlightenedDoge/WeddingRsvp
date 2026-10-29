using System.Globalization;
using System.Text.RegularExpressions;

namespace WeddingRsvp.Services;

/// <summary>Reads coordinates out of a pasted Waze or Google Maps link, so one link can drive both navigation buttons.</summary>
public static partial class MapLinks
{
    private const string GeohashAlphabet = "0123456789bcdefghjkmnpqrstuvwxyz";

    [GeneratedRegex(@"@(-?\d{1,3}\.\d+),(-?\d{1,3}\.\d+)")] private static partial Regex AtPair();
    [GeneratedRegex(@"!3d(-?\d{1,3}\.\d+)!4d(-?\d{1,3}\.\d+)")] private static partial Regex DataPair();
    [GeneratedRegex(@"^(?:ll\.)?(-?\d{1,3}(?:\.\d+)?)\s*,\s*(-?\d{1,3}(?:\.\d+)?)$")] private static partial Regex Pair();
    [GeneratedRegex(@"/ul/h([0-9b-hjkmnp-z]{4,12})(?:[/?#]|$)", RegexOptions.IgnoreCase)] private static partial Regex WazeHash();

    private static readonly string[] PairParams = ["ll", "q", "query", "destination", "daddr", "center", "to", "sll"];

    /// <summary>Coordinates found in the link itself (no network), or null.</summary>
    public static (double Lat, double Lon)? TryCoordinates(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > 2000) return null;
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u)) return null;
        var full = Uri.UnescapeDataString(u.AbsoluteUri);

        foreach (var re in new[] { DataPair(), AtPair() })
            if (re.Match(full) is { Success: true } m && Make(m.Groups[1].Value, m.Groups[2].Value) is { } c) return c;

        foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i < 1) continue;
            var key = Uri.UnescapeDataString(kv[..i]).ToLowerInvariant();
            if (!PairParams.Contains(key)) continue;
            var val = Uri.UnescapeDataString(kv[(i + 1)..].Replace('+', ' ')).Trim();
            if (Pair().Match(val) is { Success: true } m && Make(m.Groups[1].Value, m.Groups[2].Value) is { } c) return c;
        }

        if (u.Host.EndsWith("waze.com", StringComparison.OrdinalIgnoreCase) && WazeHash().Match(u.AbsolutePath) is { Success: true } w)
            return DecodeGeohash(w.Groups[1].Value.ToLowerInvariant());
        return null;
    }

    /// <summary>"31.7683, 35.2137" typed by hand (comma or space separated).</summary>
    public static (double Lat, double Lon)? TryPlainCoordinates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 60) return null;
        var m = Pair().Match(text.Trim().Replace(' ', ','));
        if (!m.Success) m = Pair().Match(text.Trim());
        return m.Success ? Make(m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    private static (double, double)? Make(string lat, string lon) =>
        double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
        && double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
        && a is >= -90 and <= 90 && b is >= -180 and <= 180 && !(a == 0 && b == 0)
            ? (Math.Round(a, 6), Math.Round(b, 6)) : null;

    /// <summary>Centre of a geohash cell (Waze share links look like waze.com/ul/h&lt;geohash&gt;).</summary>
    public static (double, double)? DecodeGeohash(string hash)
    {
        double latMin = -90, latMax = 90, lonMin = -180, lonMax = 180;
        var even = true;
        foreach (var ch in hash)
        {
            var idx = GeohashAlphabet.IndexOf(ch);
            if (idx < 0) return null;
            for (var bit = 4; bit >= 0; bit--)
            {
                var on = (idx >> bit & 1) == 1;
                if (even) { var mid = (lonMin + lonMax) / 2; if (on) lonMin = mid; else lonMax = mid; }
                else { var mid = (latMin + latMax) / 2; if (on) latMin = mid; else latMax = mid; }
                even = !even;
            }
        }
        return Make(((latMin + latMax) / 2).ToString(CultureInfo.InvariantCulture), ((lonMin + lonMax) / 2).ToString(CultureInfo.InvariantCulture));
    }

    // --- Link validation (only these ever reach a guest's page)

    /// <summary>An https link on a Waze host (waze.com, its subdomains, or the waze.page.link short links).</summary>
    public static bool IsWazeLink(string? v) => Https(v) is { } h && (h is "waze.com" or "waze.page.link" || h.EndsWith(".waze.com"));

    /// <summary>An https Google Maps link: google.com/maps, maps.google.com, maps.app.goo.gl or goo.gl/maps (also google.co.il).</summary>
    public static bool IsGoogleMapsLink(string? v)
    {
        if (Https(v) is not { } h) return false;
        var path = new Uri(v!.Trim()).AbsolutePath;
        if (h is "maps.app.goo.gl" or "maps.google.com") return true;
        if (h is "goo.gl") return path.StartsWith("/maps", StringComparison.OrdinalIgnoreCase);
        var google = h is "google.com" or "www.google.com" or "google.co.il" or "www.google.co.il";
        return google && path.StartsWith("/maps", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Https(string? v)
    {
        if (string.IsNullOrWhiteSpace(v) || v.Length > 500) return null;
        if (!Uri.TryCreate(v.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0) return null;
        return u.Host.ToLowerInvariant();
    }
}

/// <summary>
/// Follows a short map link (maps.app.goo.gl, waze.page.link) to the long one and reads the coordinates from it.
/// Only allow-listed hosts are contacted, over https, at most a few hops, and only the redirect headers are read.
/// </summary>
public sealed class MapLinkResolver(HttpClient http)
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "maps.app.goo.gl", "goo.gl", "waze.page.link", "waze.com", "www.waze.com", "ul.waze.com",
        "google.com", "www.google.com", "maps.google.com", "google.co.il", "www.google.co.il",
    };

    public async Task<(double Lat, double Lon)?> ResolveAsync(string? link, CancellationToken ct = default)
    {
        if (MapLinks.TryCoordinates(link) is { } direct) return direct;
        var current = link?.Trim();
        for (var hop = 0; hop < 4; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(u.Host)) return null;
            try
            {
                using var res = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.Headers.Location is not { } loc) return null;
                current = (loc.IsAbsoluteUri ? loc : new Uri(u, loc)).AbsoluteUri;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
            if (MapLinks.TryCoordinates(current) is { } found) return found;
        }
        return null;
    }
}
