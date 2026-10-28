namespace WeddingRsvp.Services.Sms;

/// <summary>Dev provider: writes the message to the log instead of sending it.</summary>
public sealed class LogSmsProvider(ILogger<LogSmsProvider> log) : ISmsProvider
{
    public string Name => "Log";

    public Task<SmsSendResult> SendAsync(SmsMessage m, CancellationToken ct)
    {
        log.LogInformation("[FAKE SMS] to {To} from {From}: {Body}", m.ToE164, m.SenderName, m.Body);
        return Task.FromResult(SmsSendResult.Ok("log-" + Guid.NewGuid().ToString("N")[..12]));
    }

    public Task<int?> GetBalanceAsync(CancellationToken ct) => Task.FromResult<int?>(null);
}
