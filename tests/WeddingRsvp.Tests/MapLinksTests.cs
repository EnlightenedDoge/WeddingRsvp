using System.Net;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class MapLinksTests
{
    [Theory]
    [InlineData("https://waze.com/ul?ll=32.085300,34.781800&navigate=yes", 32.0853, 34.7818)]
    [InlineData("https://www.waze.com/live-map/directions?to=ll.32.0853%2C34.7818", 32.0853, 34.7818)]
    [InlineData("https://www.google.com/maps/place/Hall/@32.0853,34.7818,17z/data=!3m1", 32.0853, 34.7818)]
    [InlineData("https://www.google.com/maps/place/Hall/data=!4m2!3d32.0853!4d34.7818", 32.0853, 34.7818)]
    [InlineData("https://www.google.com/maps/search/?api=1&query=32.0853,34.7818", 32.0853, 34.7818)]
    [InlineData("https://maps.google.com/?q=32.0853,34.7818", 32.0853, 34.7818)]
    public void Reads_coordinates_from_known_link_shapes(string link, double lat, double lon)
    {
        var c = MapLinks.TryCoordinates(link);
        Assert.NotNull(c);
        Assert.Equal(lat, c!.Value.Lat, 4);
        Assert.Equal(lon, c.Value.Lon, 4);
    }

    [Fact]
    public void Decodes_a_waze_geohash_link()
    {
        // Geohash of 32.0853,34.7818 (computed independently).
        var c = MapLinks.TryCoordinates("https://waze.com/ul/hsv8wx2zq6");
        Assert.NotNull(c);
        Assert.Equal(32.0853, c!.Value.Lat, 3);
        Assert.Equal(34.7818, c.Value.Lon, 3);
    }

    [Theory]
    [InlineData("https://waze.com/ul?q=Hall")]
    [InlineData("https://maps.app.goo.gl/abc")]
    [InlineData("https://www.google.com/maps?q=0,0")]
    [InlineData("https://x.com/?ll=999,999")]
    [InlineData("not a url")]
    public void No_coordinates_when_the_link_has_none(string link) => Assert.Null(MapLinks.TryCoordinates(link));

    [Theory]
    [InlineData("https://maps.app.goo.gl/abc", true)]
    [InlineData("https://www.google.com/maps/place/x", true)]
    [InlineData("https://goo.gl/maps/abc", true)]
    [InlineData("https://google.com/search?q=x", false)]
    [InlineData("https://goo.gl/other", false)]
    [InlineData("https://maps.google.com.evil.com/", false)]
    [InlineData("http://maps.app.goo.gl/abc", false)]
    public void Google_maps_hosts_only(string link, bool ok) => Assert.Equal(ok, MapLinks.IsGoogleMapsLink(link));

    private sealed class Redirects(Dictionary<string, string> map) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Asked.Add(r.RequestUri!.AbsoluteUri);
            var res = new HttpResponseMessage(HttpStatusCode.Redirect);
            if (map.TryGetValue(r.RequestUri.AbsoluteUri, out var to)) res.Headers.Location = new Uri(to);
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task Short_links_are_followed_through_allowed_hosts_only()
    {
        var h = new Redirects(new() { ["https://maps.app.goo.gl/abc"] = "https://www.google.com/maps/place/Hall/@32.0853,34.7818,17z" });
        var r = new MapLinkResolver(new HttpClient(h));
        var c = await r.ResolveAsync("https://maps.app.goo.gl/abc");
        Assert.Equal(32.0853, c!.Value.Lat, 4);

        var evil = new Redirects(new() { ["https://maps.app.goo.gl/x"] = "https://evil.example/steal" });
        Assert.Null(await new MapLinkResolver(new HttpClient(evil)).ResolveAsync("https://maps.app.goo.gl/x"));
        Assert.Equal(["https://maps.app.goo.gl/x"], evil.Asked); // never contacted the other host
        Assert.Empty(new Redirects([]).Asked);
        Assert.Null(await new MapLinkResolver(new HttpClient(new Redirects([]))).ResolveAsync("https://evil.example/a"));
    }
}
