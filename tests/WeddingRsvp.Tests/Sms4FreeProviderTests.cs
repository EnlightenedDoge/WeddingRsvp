using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;

namespace WeddingRsvp.Tests;

public sealed class Sms4FreeProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastBody = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return await f(r);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (Sms4FreeProvider p, Handler h) Make(Func<HttpRequestMessage, Task<HttpResponseMessage>> f)
    {
        var h = new Handler(f);
        var opts = Microsoft.Extensions.Options.Options.Create(new SmsOptions { ApiUser = "0501111111", ApiPassword = "pw", ApiKey = "k" });
        return (new Sms4FreeProvider(new HttpClient(h), opts), h);
    }

    private static readonly SmsMessage Msg = new("+972501234567", "שלום", "DanaYossi");

    [Fact]
    public async Task Sends_the_documented_fields_with_a_local_recipient()
    {
        var (p, h) = Make(_ => Task.FromResult(Json("""{"status":1,"message":""}""")));
        var r = await p.SendAsync(Msg, default);
        Assert.Equal(SmsFailureKind.Sent, r.Kind);
        var doc = JsonDocument.Parse(h.LastBody!).RootElement;
        Assert.Equal("0501234567", doc.GetProperty("recipient").GetString());
        Assert.Equal("שלום", doc.GetProperty("msg").GetString());
        Assert.Equal("DanaYossi", doc.GetProperty("sender").GetString());
        Assert.Equal("k", doc.GetProperty("key").GetString());
        Assert.Equal("0501111111", doc.GetProperty("user").GetString());
        Assert.Equal("pw", doc.GetProperty("pass").GetString());
    }

    [Theory]
    [InlineData(-1, SmsFailureKind.AccountLevel)]
    [InlineData(-2, SmsFailureKind.AccountLevel)]
    [InlineData(-3, SmsFailureKind.Rejected)]
    [InlineData(-4, SmsFailureKind.AccountLevel)]
    [InlineData(-5, SmsFailureKind.Rejected)]
    [InlineData(-6, SmsFailureKind.AccountLevel)]
    [InlineData(0, SmsFailureKind.Unknown)]
    [InlineData(1, SmsFailureKind.Sent)]
    public void Status_codes_map_to_failure_kinds(int status, SmsFailureKind kind) =>
        Assert.Equal(kind, Sms4FreeProvider.Classify(status, "m").Kind);

    [Fact]
    public async Task Socket_failure_is_transient()
    {
        var (p, _) = Make(_ => throw new HttpRequestException("refused", new SocketException()));
        Assert.Equal(SmsFailureKind.Transient, (await p.SendAsync(Msg, default)).Kind);
    }

    [Fact]
    public async Task Timeout_http_error_and_garbage_are_unknown()
    {
        var (t, _) = Make(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(SmsFailureKind.Unknown, (await t.SendAsync(Msg, default)).Kind);
        var (e, _) = Make(_ => Task.FromResult(Json("oops", HttpStatusCode.InternalServerError)));
        Assert.Equal(SmsFailureKind.Unknown, (await e.SendAsync(Msg, default)).Kind);
        var (g, _) = Make(_ => Task.FromResult(Json("not json")));
        Assert.Equal(SmsFailureKind.Unknown, (await g.SendAsync(Msg, default)).Kind);
    }

    [Fact]
    public async Task Non_israeli_numbers_are_rejected_without_a_request()
    {
        var (p, h) = Make(_ => throw new InvalidOperationException("must not be called"));
        var r = await p.SendAsync(Msg with { ToE164 = "+14155550100" }, default);
        Assert.Equal(SmsFailureKind.Rejected, r.Kind);
        Assert.Null(h.LastBody);
    }

    [Fact]
    public async Task Balance_parses_an_integer_and_treats_negative_as_unknown()
    {
        var (ok, _) = Make(_ => Task.FromResult(new HttpResponseMessage { Content = new StringContent("9850") }));
        Assert.Equal(9850, await ok.GetBalanceAsync(default));
        var (bad, _) = Make(_ => Task.FromResult(new HttpResponseMessage { Content = new StringContent("-1") }));
        Assert.Null(await bad.GetBalanceAsync(default));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(134, 1)] [InlineData(135, 2)] [InlineData(201, 2)] [InlineData(202, 3)]
    public void Credits_follow_the_134_plus_67_rule(int length, int credits) =>
        Assert.Equal(credits, SmsSegments.Credits(new string('א', length)));
}
