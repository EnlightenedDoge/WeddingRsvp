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

    // Yom tov days, 5786 (not a leap year) and 5787 (leap year), so the Nisan/Sivan month shift is covered.
    [Theory]
    [InlineData("2026-04-02")] // Pesach, first day (15 Nisan 5786)
    [InlineData("2026-04-08")] // Pesach, seventh day
    [InlineData("2026-05-22")] // Shavuot 5786
    [InlineData("2026-09-12")] // Rosh Hashana 5787
    [InlineData("2026-09-13")] // Rosh Hashana, second day
    [InlineData("2026-09-21")] // Yom Kippur
    [InlineData("2026-09-26")] // Sukkot, first day
    [InlineData("2026-10-03")] // Shemini Atzeret / Simchat Torah
    [InlineData("2027-04-22")] // Pesach 5787 (leap year)
    [InlineData("2027-04-28")] // Pesach, seventh day
    [InlineData("2027-06-11")] // Shavuot 5787 (6 Sivan, a Friday)
    public void Yom_tov_is_never_allowed(string date)
    {
        Assert.True(JewishHolidays.IsYomTov(DateTime.Parse(date)));
        Assert.False(SendingWindow.IsAllowed(DateTime.Parse(date + " 12:00"), O));
    }

    [Theory]
    [InlineData("2026-09-14")] // day after Rosh Hashana
    [InlineData("2026-09-27")] // Sukkot, chol hamoed
    [InlineData("2027-04-25")] // Pesach, chol hamoed (leap year)
    [InlineData("2027-06-13")] // day after Shavuot
    public void Ordinary_and_chol_hamoed_days_are_allowed(string date)
    {
        Assert.False(JewishHolidays.IsYomTov(DateTime.Parse(date)));
        Assert.True(SendingWindow.IsAllowed(DateTime.Parse(date + " 12:00"), O));
    }

    // Sunday 2026-09-20 is the eve of Yom Kippur: it stops at the Friday cutoff.
    [Theory]
    [InlineData("2026-09-20 12:59", true)]
    [InlineData("2026-09-20 13:00", false)]
    [InlineData("2026-09-20 09:00", true)]
    public void Eve_of_yom_tov_stops_early(string local, bool allowed) =>
        Assert.Equal(allowed, SendingWindow.IsAllowed(DateTime.Parse(local), O));
}
