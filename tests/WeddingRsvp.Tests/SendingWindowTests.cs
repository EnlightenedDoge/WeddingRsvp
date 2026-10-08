using WeddingRsvp.Options;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class SendingWindowTests
{
    private static readonly SendingOptions O = new();

    // 2026-10-11 is a Sunday.
    [Theory]
    [InlineData("2026-10-11 09:00", true)]
    [InlineData("2026-10-11 08:59", false)]
    [InlineData("2026-10-11 20:59", true)]
    [InlineData("2026-10-11 21:00", false)]
    [InlineData("2026-10-16 12:59", true)]   // Friday before cutoff
    [InlineData("2026-10-16 13:00", false)]  // Friday after cutoff
    [InlineData("2026-10-17 12:00", false)]  // Saturday
    [InlineData("2026-10-17 22:00", false)]
    public void Respects_window(string local, bool allowed) =>
        Assert.Equal(allowed, SendingWindow.IsAllowed(DateTime.Parse(local), O));
}
