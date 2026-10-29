using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class WazeLinkTests
{
    [Theory]
    [InlineData("https://waze.com/ul/hsv8wm6xyz", true)]
    [InlineData("https://www.waze.com/live-map/directions?to=ll.32.08%2C34.78", true)]
    [InlineData("https://ul.waze.com/ul?ll=32.08,34.78", true)]
    [InlineData("https://waze.page.link/abc", true)]
    [InlineData("http://waze.com/ul/h", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://evil.com/waze.com", false)]
    [InlineData("https://waze.com.evil.com/ul", false)]
    [InlineData("https://user@waze.com/ul", false)]
    [InlineData("", false)]
    public void Only_https_waze_hosts_are_accepted(string v, bool ok) => Assert.Equal(ok, MapLinks.IsWazeLink(v));

    [Fact]
    public void Coordinates_drive_both_buttons_and_beat_pasted_links()
    {
        var s = new EventSettings { Latitude = 32.1, Longitude = 34.8, WazeLink = "https://waze.com/ul/habc", GoogleMapsLink = "https://maps.app.goo.gl/x" };
        Assert.Equal("https://waze.com/ul?ll=32.1,34.8&navigate=yes", CalendarLinks.WazeUrl(s));
        Assert.Equal("https://www.google.com/maps/search/?api=1&query=32.1,34.8", CalendarLinks.GoogleMapsUrl(s));
    }

    [Fact]
    public void Without_coordinates_pasted_links_are_used_and_bad_ones_ignored()
    {
        var s = new EventSettings { WazeLink = "https://waze.com/ul/habc", GoogleMapsLink = "https://maps.app.goo.gl/x" };
        Assert.Equal("https://waze.com/ul/habc", CalendarLinks.WazeUrl(s));
        Assert.Equal("https://maps.app.goo.gl/x", CalendarLinks.GoogleMapsUrl(s));
        s.WazeLink = "https://evil.com/x"; s.GoogleMapsLink = "https://evil.com/maps";
        s.VenueName = "Hall";
        Assert.StartsWith("https://waze.com/ul?q=Hall", CalendarLinks.WazeUrl(s));
        Assert.StartsWith("https://www.google.com/maps/search/?api=1&query=Hall", CalendarLinks.GoogleMapsUrl(s));
    }
}
