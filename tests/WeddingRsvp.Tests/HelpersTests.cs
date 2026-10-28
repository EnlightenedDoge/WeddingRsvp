using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class HelpersTests
{
    [Fact]
    public void Tokens_are_valid_and_distinct()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => TokenGenerator.New()).ToList();
        Assert.All(tokens, t => Assert.True(TokenGenerator.LooksValid(t)));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Waze_prefers_coordinates()
    {
        var s = new EventSettings { Latitude = 31.7683, Longitude = 35.2137, VenueName = "אולם" };
        Assert.Equal("https://waze.com/ul?ll=31.7683,35.2137&navigate=yes", CalendarLinks.WazeUrl(s));
    }

    [Fact]
    public void Template_renders_placeholders()
    {
        var s = new EventSettings { CoupleNames = "דנה ויוסי", StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0) };
        var text = MessageTemplates.Render("{name}|{couple}|{date}|{link}", "משפחת כהן", s, "https://x/ab");
        Assert.Equal("משפחת כהן|דנה ויוסי|15/03|https://x/ab", text);
    }

    [Fact]
    public void Csv_parses_quotes_and_hebrew_headers()
    {
        var rows = CsvReader.Parse("שם,טלפון,מוזמנים\r\n\"כהן, משפחת\",050-1234567,4\r\n", ',');
        Assert.Equal(2, rows.Count);
        Assert.Equal("כהן, משפחת", rows[1][0]);
        Assert.Equal("4", rows[1][2]);
    }

    [Fact]
    public void Ics_is_folded_and_crlf_terminated()
    {
        var s = new EventSettings { Title = new string('ח', 60), StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0),
                                    EndsAtLocal = new DateTime(2027, 3, 15, 23, 0, 0) };
        var ics = CalendarLinks.BuildIcs(s, "https://rsvp.example.net", DateTime.UtcNow);
        foreach (var line in ics.Split("\r\n"))
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line);
        Assert.Contains("DTSTART:20270315T170000Z", ics); // 19:00 Israel standard time = 17:00 UTC
    }
}

public class PortGuardTests
{
    [Theory]
    [InlineData("/admin/site", true)]
    [InlineData("/admin/site/couples", true)]
    [InlineData("/ADMIN/SITE/couples/3", true)]
    [InlineData("/admin", false)]
    [InlineData("/admin/guests", false)]
    [InlineData("/admin/event", false)]
    [InlineData("/admin/sitemap", false)]
    [InlineData("/signin", false)]
    public void Only_site_admin_pages_are_private(string path, bool siteAdmin) =>
        Assert.Equal(siteAdmin, PortGuard.IsSiteAdminPath(path));

    [Theory]
    [InlineData("/signin", true)]
    [InlineData("/signin/2fa", true)]
    [InlineData("/signout", true)]
    [InlineData("/set-password", true)]
    [InlineData("/admin/guests", true)]
    [InlineData("/AbCdEfGhJk", false)]
    [InlineData("/healthz", false)]
    public void Portal_paths(string path, bool portal) => Assert.Equal(portal, PortGuard.IsPortalPath(path));

    [Theory]
    [InlineData("/signin", true)]
    [InlineData("/set-password", true)]
    [InlineData("/signin/2fa", true)]
    [InlineData("/signout", false)]
    [InlineData("/admin/guests", false)]
    public void Credential_paths(string path, bool credential) => Assert.Equal(credential, PortGuard.IsCredentialPath(path));

    // development, onAdminPort, adminAllowPlainHttp, path, isHttps -> refused
    [Theory]
    [InlineData(false, false, false, "/signin", false, true)]    // public port, plain HTTP: refused
    [InlineData(false, false, true, "/signin", false, true)]     // the opt-in never applies to the public port
    [InlineData(false, false, false, "/signin", true, false)]    // public port via the proxy with HTTPS: fine
    [InlineData(false, false, false, "/admin/guests", false, true)]
    [InlineData(false, false, false, "/AbCdEfGhJk", false, false)] // guest pages don't need HTTPS here
    [InlineData(false, false, false, "/healthz", false, false)]
    [InlineData(false, true, false, "/signin", false, true)]     // admin port, opt-in off: refused
    [InlineData(false, true, true, "/signin", false, false)]     // admin port, opt-in on: allowed
    [InlineData(false, true, true, "/admin/site/couples", false, false)]
    [InlineData(true, false, false, "/signin", false, false)]    // Development: never refused
    public void Https_is_required_except_on_the_opt_in_admin_port(bool dev, bool admin, bool allow, string path, bool https, bool refused) =>
        Assert.Equal(refused, PortGuard.RequiresHttps(dev, admin, allow, path, https));
}
