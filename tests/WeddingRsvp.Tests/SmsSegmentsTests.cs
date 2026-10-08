using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class SmsSegmentsTests
{
    [Fact]
    public void Latin_text_uses_gsm7_limits()
    {
        Assert.Equal(1, SmsSegments.Count(new string('a', 160)).Segments);
        Assert.Equal(2, SmsSegments.Count(new string('a', 161)).Segments);
    }

    [Fact]
    public void Hebrew_forces_ucs2_limits()
    {
        var r = SmsSegments.Count(new string('א', 70));
        Assert.True(r.Unicode);
        Assert.Equal(1, r.Segments);
        Assert.Equal(2, SmsSegments.Count(new string('א', 71)).Segments);
        Assert.Equal(3, SmsSegments.Count(new string('א', 135)).Segments);
    }
}
