namespace WeddingRsvp.Services.Sms;

/// <param name="ClientMessageId">Our own id for the message (MessageLog.Id); providers that support delivery reports echo it back.</param>
public sealed record SmsMessage(string ToE164, string Body, string? SenderName, string? ClientMessageId = null);

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

/// <summary>
/// How a provider bills a message of a given length: up to <see cref="SingleLimit"/> characters cost 1 credit, then 1 more
/// per <see cref="PartLimit"/>. <see cref="Estimated"/> marks a rule nobody has verified against a real balance yet.
/// </summary>
public sealed record SmsCostRule(int SingleLimit, int PartLimit, bool Estimated = false)
{
    public int Credits(int length) => length <= SingleLimit ? 1 : 1 + (int)Math.Ceiling((length - SingleLimit) / (double)PartLimit);
}

/// <summary>Provider-neutral SMS gateway. Field names, phone formats and status codes stay inside the implementation.</summary>
public interface ISmsProvider
{
    string Name { get; }
    Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken ct);
    /// <summary>Messages the provider account can still send, or null if unknown / the call failed.</summary>
    Task<int?> GetBalanceAsync(CancellationToken ct);
    /// <summary>The billing rule for this provider. <paramref name="unicode"/> is true when the text needs UCS-2 (e.g. Hebrew).</summary>
    SmsCostRule CostRule(bool unicode);
}

public static class SmsProviderExtensions
{
    /// <summary>Credits the active provider will bill for this exact text.</summary>
    public static int Credits(this ISmsProvider provider, string text)
    {
        var info = SmsSegments.Count(text);
        return provider.CostRule(info.Unicode).Credits(info.Length);
    }
}
