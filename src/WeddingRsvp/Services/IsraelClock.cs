namespace WeddingRsvp.Services;

/// <summary>Single source of "now" plus Israel-time conversions. Inject TimeProvider in tests.</summary>
public sealed class IsraelClock(TimeProvider time)
{
    public static readonly TimeZoneInfo Zone = FindZone();

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    public DateTime LocalNow => ToLocal(UtcNow);

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time"); }
    }
}
