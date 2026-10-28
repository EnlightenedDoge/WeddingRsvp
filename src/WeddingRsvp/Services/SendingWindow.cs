using Microsoft.Extensions.Options;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services;

/// <summary>
/// When messages may go out (Israel local time): daytime only; Friday and the day before a yom tov until early
/// afternoon; never on Saturday or on a yom tov (see JewishHolidays). Other special days: use the "pause sending" switch.
/// </summary>
public sealed class SendingWindow(IOptions<SendingOptions> options)
{
    public bool IsAllowed(DateTime local) => IsAllowed(local, options.Value);

    public static bool IsAllowed(DateTime local, SendingOptions o)
    {
        var h = local.Hour;
        if (local.DayOfWeek == DayOfWeek.Saturday || JewishHolidays.IsYomTov(local.Date)) return false;
        var endHour = local.DayOfWeek == DayOfWeek.Friday || JewishHolidays.IsErev(local.Date) ? o.FridayEndHour : o.DayEndHour;
        return h >= o.DayStartHour && h < endHour;
    }
}
