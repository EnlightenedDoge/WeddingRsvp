using Microsoft.Extensions.Options;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services.Sms;

/// <summary>
/// TODO: implement against SMS4FREE — full spec in SPEC.md §6 (one recipient per request, no auto-retry on timeouts).
/// Requirements: Hebrew (UCS-2) bodies, alphanumeric sender name, return the provider's message id
/// (used to match delivery reports), and map provider errors to SmsSendResult.Fail.
/// Until implemented, every send fails loudly so nothing is silently "sent".
/// </summary>
public sealed class HttpSmsSender(HttpClient http, IOptions<SmsOptions> options, ILogger<HttpSmsSender> log) : ISmsSender
{
    public Task<SmsSendResult> SendAsync(SmsMessage m, CancellationToken ct)
    {
        _ = http; _ = options;
        log.LogError("HttpSmsSender is not implemented yet; message to {To} not sent", m.ToE164);
        return Task.FromResult(SmsSendResult.Fail("SMS provider not implemented"));
    }
}
