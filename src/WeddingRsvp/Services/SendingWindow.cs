using Microsoft.Extensions.Options;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services;

/// <summary>
/// When messages may go out (Israel local time): daytime only, Friday until early afternoon,
/// never on Saturday. Holidays are not modeled; use the "pause sending" switch for those.
/// </summary>
public sealed class SendingWindow(IOptions<SendingOptions> options)
{
    public bool IsAllowed(DateTime local) => IsAllowed(local, options.Value);

    public static bool IsAllowed(DateTime local, SendingOptions o)
    {
        var h = local.Hour;
        return local.DayOfWeek switch
        {
            DayOfWeek.Saturday => false,
            DayOfWeek.Friday => h >= o.DayStartHour && h < o.FridayEndHour,
            _ => h >= o.DayStartHour && h < o.DayEndHour,
        };
    }
}
