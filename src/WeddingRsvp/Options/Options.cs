namespace WeddingRsvp.Options;

public sealed class AppOptions
{
    /// <summary>Public origin used to build guest links, e.g. https://rsvp.example.com</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    /// <summary>Origin of the sign-in/admin site, used for one-time set-password links.</summary>
    public string AdminBaseUrl { get; set; } = "http://localhost:8081";
    /// <summary>On an empty database, a site admin with these credentials is created. Remove both afterwards.</summary>
    public string? BootstrapAdminEmail { get; set; }
    public string? BootstrapAdminPassword { get; set; }
    /// <summary>Holds rsvp.db and media/. Mounted as a volume in Docker.</summary>
    public string DataDir { get; set; } = "data";
    /// <summary>Guest-facing port (rsvp.* proxy host points here).</summary>
    public int PublicPort { get; set; } = 8080;
    /// <summary>Admin port (admin.* proxy host with an NPM access list points here).</summary>
    public int AdminPort { get; set; } = 8081;
    /// <summary>Secret path segment for the SMS delivery-report webhook. Empty disables it.</summary>
    public string? DeliveryWebhookSecret { get; set; }
}

public sealed class SmsOptions
{
    /// <summary>"Log" (dev: writes messages to the log) or "Sms4Free" (see Sms4FreeProvider).</summary>
    public string Provider { get; set; } = "Log";
    /// <summary>SMS4FREE send endpoint (see SPEC.md §6).</summary>
    public string ApiUrl { get; set; } = "https://api.sms4free.co.il/ApiSMS/v2/SendSMS";
    public string BalanceUrl { get; set; } = "https://api.sms4free.co.il/ApiSMS/AvailableSMS";
    /// <summary>SMS4FREE "user": the mobile number the account was registered with.</summary>
    public string? ApiUser { get; set; }
    public string? ApiPassword { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class SendingOptions
{
    public int TickSeconds { get; set; } = 20;
    /// <summary>Max messages per tick (throttle).</summary>
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 3;

    // Sending window, Israel local time. Nothing is sent on Saturday.
    public int DayStartHour { get; set; } = 9;
    public int DayEndHour { get; set; } = 21;
    public int FridayEndHour { get; set; } = 13;
}
