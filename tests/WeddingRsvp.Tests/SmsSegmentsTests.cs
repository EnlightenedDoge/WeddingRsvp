using WeddingRsvp.Services.Sms;
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

    [Fact]
    public void Each_provider_has_its_own_cost_rule()
    {
        var log = new WeddingRsvp.Services.Sms.LogSmsProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<WeddingRsvp.Services.Sms.LogSmsProvider>.Instance);
        var hebrew139 = new string('א', 139);
        Assert.Equal(3, log.Credits(hebrew139));      // network segments: 70, then 67 each
        Assert.Equal(2, new WeddingRsvp.Services.Sms.SmsCostRule(134, 67).Credits(139)); // SMS4FREE-style rule on the same length
        Assert.Equal(1, log.Credits(new string('a', 160)));
        Assert.Equal(2, log.Credits(new string('a', 161)));
    }
}
