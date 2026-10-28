namespace WeddingRsvp.Services.Sms;

public sealed record SmsMessage(string ToE164, string Body, string? SenderName);

/// <summary>How a send attempt ended. Set by the provider class, which maps its own status codes.</summary>
public enum SmsFailureKind
{
    /// <summary>Accepted by the provider.</summary>
    Sent,
    /// <summary>Provably never reached the provider (DNS, connection refused): safe to retry.</summary>
    Transient,
    /// <summary>The provider refused this message (bad number, content): don't retry.</summary>
    Rejected,
    /// <summary>Timeout or unreadable reply: it may have been sent. Never auto-retried.</summary>
    Unknown,
    /// <summary>Out of credit, bad credentials, unverified sender: affects every message (circuit breaker).</summary>
    AccountLevel,
}

public sealed record SmsSendResult(SmsFailureKind Kind, string? ProviderMessageId, string? Error)
{
    public bool Success => Kind == SmsFailureKind.Sent;
    public static SmsSendResult Ok(string? id = null) => new(SmsFailureKind.Sent, id, null);
    public static SmsSendResult Transient(string error) => new(SmsFailureKind.Transient, null, error);
    public static SmsSendResult Rejected(string error) => new(SmsFailureKind.Rejected, null, error);
    public static SmsSendResult Unknown(string error) => new(SmsFailureKind.Unknown, null, error);
    public static SmsSendResult AccountLevel(string error) => new(SmsFailureKind.AccountLevel, null, error);
}

/// <summary>Provider-neutral SMS gateway. Field names, phone formats and status codes stay inside the implementation.</summary>
public interface ISmsProvider
{
    string Name { get; }
    Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken ct);
    /// <summary>Messages the provider account can still send, or null if unknown / the call failed.</summary>
    Task<int?> GetBalanceAsync(CancellationToken ct);
}
