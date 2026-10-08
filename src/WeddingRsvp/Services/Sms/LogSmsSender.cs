namespace WeddingRsvp.Services.Sms;

/// <summary>Dev sender: writes the message to the log instead of sending it.</summary>
public sealed class LogSmsSender(ILogger<LogSmsSender> log) : ISmsSender
{
    public Task<SmsSendResult> SendAsync(SmsMessage m, CancellationToken ct)
    {
        log.LogInformation("[FAKE SMS] to {To} from {From}: {Body}", m.ToE164, m.SenderName, m.Body);
        return Task.FromResult(SmsSendResult.Ok("log-" + Guid.NewGuid().ToString("N")[..12]));
    }
}
