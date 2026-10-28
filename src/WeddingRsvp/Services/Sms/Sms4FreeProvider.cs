using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services.Sms;

/// <summary>
/// SMS4FREE (https://sms4free.co.il/docs/otp-api.html). One recipient per request. The API returns an accepted count,
/// not a message id, so ProviderMessageId stays null.
/// </summary>
public sealed class Sms4FreeProvider(HttpClient http, IOptions<SmsOptions> options) : ISmsProvider
{
    public string Name => "Sms4Free";

    public async Task<SmsSendResult> SendAsync(SmsMessage m, CancellationToken ct)
    {
        if (ToRecipient(m.ToE164) is not { } recipient)
            return SmsSendResult.Rejected("Non-Israeli number: send manually");

        var o = options.Value;
        var body = new { key = o.ApiKey, user = o.ApiUser, pass = o.ApiPassword, sender = m.SenderName ?? "", recipient, msg = m.Body };
        try
        {
            using var res = await http.PostAsJsonAsync(o.ApiUrl, body, ct);
            if (!res.IsSuccessStatusCode) return SmsSendResult.Unknown($"HTTP {(int)res.StatusCode}");
            var reply = await res.Content.ReadFromJsonAsync<Reply>(ct);
            return reply is null ? SmsSendResult.Unknown("Unreadable reply") : Classify(reply.Status, reply.Message);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            return SmsSendResult.Transient(ex.Message); // DNS failure / connection refused: the request never arrived
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            if (ct.IsCancellationRequested) throw;
            return SmsSendResult.Unknown(ex.Message); // may have been sent: never auto-retried
        }
    }

    public async Task<int?> GetBalanceAsync(CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            using var res = await http.PostAsJsonAsync(o.BalanceUrl, new { key = o.ApiKey, user = o.ApiUser, pass = o.ApiPassword }, ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = (await res.Content.ReadAsStringAsync(ct)).Trim();
            return int.TryParse(text, out var n) && n >= 0 ? n : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Maps the API's status codes (see the docs page).</summary>
    public static SmsSendResult Classify(int status, string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? $"status {status}" : $"{status}: {message}";
        return status switch
        {
            > 0 => SmsSendResult.Ok(),
            -1 or -2 or -4 or -6 => SmsSendResult.AccountLevel(text), // credentials, sender id, no balance, sender unverified
            -3 or -5 => SmsSendResult.Rejected(text),                 // no valid recipient, invalid content
            _ => SmsSendResult.Unknown(text),                         // 0 = general error: don't guess
        };
    }

    /// <summary>
    /// The recipient exactly as stored (+9725XXXXXXXX, country code first; the API accepts it). Null for anything
    /// that isn't an Israeli mobile number, because the API only sends to Israeli numbers.
    /// </summary>
    public static string? ToRecipient(string e164) =>
        e164.StartsWith("+9725", StringComparison.Ordinal) && e164.Length == 13 && e164[1..].All(char.IsDigit)
            ? e164 : null;

    private sealed record Reply(int Status, string? Message);
}
