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

    /// <summary>Dev provider: plain network segments (UCS-2 70/67, GSM-7 160/153).</summary>
    public SmsCostRule CostRule(bool unicode) => unicode ? new(70, 67) : new(160, 153);

    public Task<int?> GetBalanceAsync(CancellationToken ct) => Task.FromResult<int?>(null);
}
