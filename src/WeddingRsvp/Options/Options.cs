namespace WeddingRsvp.Options;

public sealed class AppOptions
{
    /// <summary>Public origin used to build guest links and couples' one-time set-password links, e.g. https://example.com</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    /// <summary>On an empty database, a site admin with these credentials is created. Remove both afterwards.</summary>
    public string? BootstrapAdminEmail { get; set; }
    public string? BootstrapAdminPassword { get; set; }
    /// <summary>Holds rsvp.db and media/. Mounted as a volume in Docker.</summary>
    public string DataDir { get; set; } = "data";
    /// <summary>
    /// Lets the private admin port serve sign-in over plain HTTP (a LAN-only site admin with no certificate). Off by default; the
    /// public port always requires HTTPS. The admin password then crosses your network unencrypted: only on a network you trust.
    /// </summary>
    public bool AdminAllowPlainHttp { get; set; }
    /// <summary>Public port: guest pages and the couples' sign-in and portal. The public proxy host points here.</summary>
    public int PublicPort { get; set; } = 8080;
    /// <summary>Private port: site-admin pages (/admin/site/**) exist only here. Do not expose it to the internet.</summary>
    public int AdminPort { get; set; } = 8081;
    /// <summary>Secret path segment for the SMS delivery-report webhook. Empty disables it.</summary>
    public string? DeliveryWebhookSecret { get; set; }

    /// <summary>
    /// Cloudflare Turnstile (SPEC.md section 13). The site key is public; the secret key lives in .env only. Turnstile is ON only when
    /// both are set. It guards the couples' sign-in pages and the invitation's "seen" report on the public port, never the private admin port.
    /// </summary>
    public string? TurnstileSiteKey { get; set; }
    public string? TurnstileSecretKey { get; set; }
    /// <summary>
    /// Hostnames Cloudflare may report for a valid token, comma-separated. Empty = the host of <see cref="PublicBaseUrl"/>. Never put
    /// localhost here in production.
    /// </summary>
    public string? TurnstileAllowedHostnames { get; set; }
    /// <summary>Test-only: replaces Cloudflare's siteverify address (a local fake). Leave empty in production.</summary>
    public string? TurnstileVerifyUrl { get; set; }
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
