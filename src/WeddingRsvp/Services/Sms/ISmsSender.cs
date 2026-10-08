namespace WeddingRsvp.Services.Sms;

public sealed record SmsMessage(string ToE164, string Body, string? SenderName);

public sealed record SmsSendResult(bool Success, string? ProviderMessageId, string? Error)
{
    public static SmsSendResult Ok(string? id) => new(true, id, null);
    public static SmsSendResult Fail(string error) => new(false, null, error);
}

public interface ISmsSender
{
    Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken ct);
}
