using System.Globalization;

namespace WeddingRsvp.Services;

/// <summary>
/// Israeli yom tov days (work-prohibited): Rosh Hashana (2 days), Yom Kippur, Sukkot (first day), Shemini Atzeret /
/// Simchat Torah, Pesach (first and seventh day), Shavuot. Computed from the Hebrew calendar, so no yearly list to maintain.
/// Dates are civil dates: the holiday's evening before it is covered by treating the day before as an "erev".
/// </summary>
public static class JewishHolidays
{
    private static readonly HebrewCalendar Calendar = new();

    public static bool IsYomTov(DateTime date)
    {
        try
        {
            var year = Calendar.GetYear(date);
            var month = Calendar.GetMonth(date);
            var day = Calendar.GetDayOfMonth(date);
            // .NET numbers months from Tishrei; Adar II is month 7 in leap years, which shifts Nisan and Sivan by one.
            var leap = Calendar.IsLeapYear(year);
            var nisan = leap ? 8 : 7;
            var sivan = leap ? 10 : 9;
            return (month, day) switch
            {
                (1, 1 or 2 or 10 or 15 or 22) => true, // Tishrei: Rosh Hashana, Yom Kippur, Sukkot, Shemini Atzeret
                _ when month == nisan && day is 15 or 21 => true,
                _ when month == sivan && day == 6 => true,
                _ => false,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // outside the Hebrew calendar's supported range
        }
    }

    /// <summary>The day before a yom tov: messages stop early, like on Friday.</summary>
    public static bool IsErev(DateTime date) => IsYomTov(date.Date.AddDays(1));
}
