using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Options;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public sealed class TurnstileVerifierTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }
        public Uri? LastUrl { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            LastUrl = r.RequestUri;
            LastBody = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return await f(r);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AppOptions Configured() => new()
    {
        PublicBaseUrl = "https://example.com", TurnstileSiteKey = "site", TurnstileSecretKey = "secret-key",
    };

    private static (TurnstileVerifier v, Handler h) Make(Func<HttpRequestMessage, Task<HttpResponseMessage>> f, AppOptions? o = null)
    {
        var h = new Handler(f);
        return (new TurnstileVerifier(new HttpClient(h), Microsoft.Extensions.Options.Options.Create(o ?? Configured()),
            NullLogger<TurnstileVerifier>.Instance), h);
    }

    private static Task<HttpResponseMessage> Ok(string action = "signin", string host = "example.com") =>
        Task.FromResult(Json($$"""{"success":true,"action":"{{action}}","hostname":"{{host}}","error-codes":[]}"""));

    [Fact]
    public async Task A_confirmed_token_for_the_right_action_and_host_passes_and_the_request_is_well_formed()
    {
        var (v, h) = Make(_ => Ok());
        Assert.Equal(TurnstileOutcome.Passed, await v.VerifyAsync("tok-123", "signin", "203.0.113.9"));
        Assert.Equal(TurnstileVerifier.VerifyUrl, h.LastUrl!.ToString());
        Assert.Contains("secret=secret-key", h.LastBody);
        Assert.Contains("response=tok-123", h.LastBody);
        Assert.Contains("remoteip=203.0.113.9", h.LastBody);
    }

    [Fact]
    public async Task The_remote_ip_is_optional()
    {
        var (v, h) = Make(_ => Ok());
        Assert.Equal(TurnstileOutcome.Passed, await v.VerifyAsync("tok", "signin", null));
        Assert.DoesNotContain("remoteip", h.LastBody);
    }

    [Theory]
    [InlineData("""{"success":false,"error-codes":["invalid-input-response"]}""")]
    [InlineData("""{"success":false,"error-codes":["timeout-or-duplicate"]}""")]
    [InlineData("""{"success":false,"error-codes":["invalid-input-secret"]}""")]
    public async Task Cloudflare_refusals_fail(string reply)
    {
        var (v, _) = Make(_ => Task.FromResult(Json(reply)));
        Assert.Equal(TurnstileOutcome.Failed, await v.VerifyAsync("tok", "signin", null));
    }

    [Fact]
    public async Task A_token_minted_for_another_action_fails()
    {
        var (v, _) = Make(_ => Ok(action: "seen"));
        Assert.Equal(TurnstileOutcome.Failed, await v.VerifyAsync("tok", "signin", null));
    }

    [Theory]
    [InlineData("evil.example.net")]
    [InlineData("localhost")]
    [InlineData("")]
    public async Task A_token_from_a_hostname_that_is_not_allowed_fails(string host)
    {
        var (v, _) = Make(_ => Ok(host: host));
        Assert.Equal(TurnstileOutcome.Failed, await v.VerifyAsync("tok", "signin", null));
    }

    [Fact]
    public async Task A_missing_token_fails_without_calling_cloudflare()
    {
        var (v, h) = Make(_ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(TurnstileOutcome.Failed, await v.VerifyAsync(null, "signin", null));
        Assert.Equal(TurnstileOutcome.Failed, await v.VerifyAsync("  ", "signin", null));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Cloudflare_being_unreachable_is_unavailable_not_failed()
    {
        var (a, _) = Make(_ => Task.FromResult(Json("oops", HttpStatusCode.BadGateway)));
        Assert.Equal(TurnstileOutcome.Unavailable, await a.VerifyAsync("tok", "signin", null));
        var (b, _) = Make(_ => throw new HttpRequestException("dns"));
        Assert.Equal(TurnstileOutcome.Unavailable, await b.VerifyAsync("tok", "signin", null));
        var (c, _) = Make(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(TurnstileOutcome.Unavailable, await c.VerifyAsync("tok", "signin", null));
        var (d, _) = Make(_ => Task.FromResult(Json("not json")));
        Assert.Equal(TurnstileOutcome.Unavailable, await d.VerifyAsync("tok", "signin", null));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("site", null, false)]      // one key alone is a misconfiguration: stays off
    [InlineData(null, "secret", false)]
    [InlineData("site", "secret", true)]
    public async Task It_is_enabled_only_when_both_keys_are_set(string? site, string? secret, bool enabled)
    {
        var (v, h) = Make(_ => throw new InvalidOperationException("must not be called"),
            new AppOptions { TurnstileSiteKey = site, TurnstileSecretKey = secret });
        Assert.Equal(enabled, v.Enabled);
        Assert.Equal(enabled ? site : null, v.SiteKey);
        if (!enabled)
        {
            Assert.Equal(TurnstileOutcome.Passed, await v.VerifyAsync(null, "signin", null)); // off: nothing to verify
            Assert.Equal(0, h.Calls);
        }
    }

    [Fact]
    public void The_allowed_hostnames_default_to_the_public_base_url_and_can_be_overridden()
    {
        Assert.Equal(new[] { "example.com" }, TurnstileVerifier.AllowedHostnames(new AppOptions { PublicBaseUrl = "https://Example.com/" }));
        var set = TurnstileVerifier.AllowedHostnames(new AppOptions
        {
            PublicBaseUrl = "https://example.com", TurnstileAllowedHostnames = "A.example.org, b.example.org;c.example.org",
        });
        Assert.Equal(new[] { "a.example.org", "b.example.org", "c.example.org" }, set.OrderBy(x => x));
    }
}

public sealed class TurnstileGateTests
{
    private sealed class FakeVerifier(bool enabled) : ITurnstileVerifier
    {
        public bool Enabled => enabled;
        public string? SiteKey => enabled ? "site" : null;
        public string? Token, Action, Ip;
        public int Calls;
        public TurnstileOutcome Result = TurnstileOutcome.Passed;
        public Task<TurnstileOutcome> VerifyAsync(string? token, string action, string? remoteIp, CancellationToken ct = default)
        {
            Calls++; Token = token; Action = action; Ip = remoteIp;
            return Task.FromResult(Result);
        }
    }

    private static (TurnstileGate gate, FakeVerifier verifier) Make(bool enabled, int localPort, string? form = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = localPort;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        if (form is not null)
        {
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(form));
        }
        var verifier = new FakeVerifier(enabled);
        var gate = new TurnstileGate(verifier, Microsoft.Extensions.Options.Options.Create(new AppOptions { PublicPort = 8080, AdminPort = 8081 }),
            new HttpContextAccessor { HttpContext = ctx });
        return (gate, verifier);
    }

    [Theory]
    [InlineData(true, 8080, true)]    // public port, keys set
    [InlineData(true, 8081, false)]   // the private admin port never depends on Cloudflare
    [InlineData(false, 8080, false)]  // keys not set
    public void It_applies_only_on_the_public_port_when_enabled(bool enabled, int port, bool active) =>
        Assert.Equal(active, Make(enabled, port).gate.Active);

    [Fact]
    public async Task It_passes_the_widgets_token_the_action_and_the_client_ip_to_the_verifier()
    {
        var (gate, v) = Make(true, 8080, "Email=a%40b.c&cf-turnstile-response=the-token");
        Assert.Equal(TurnstileOutcome.Passed, await gate.CheckFormAsync("signin"));
        Assert.Equal("the-token", v.Token);
        Assert.Equal("signin", v.Action);
        Assert.Equal("198.51.100.7", v.Ip);
    }

    [Fact]
    public async Task A_form_without_the_token_is_sent_on_as_missing()
    {
        var (gate, v) = Make(true, 8080, "Email=a%40b.c");
        v.Result = TurnstileOutcome.Failed;
        Assert.Equal(TurnstileOutcome.Failed, await gate.CheckFormAsync("signin"));
        Assert.True(string.IsNullOrEmpty(v.Token));
    }

    [Theory]
    [InlineData(true, 8081)]
    [InlineData(false, 8080)]
    public async Task Where_it_does_not_apply_nothing_is_verified(bool enabled, int port)
    {
        var (gate, v) = Make(enabled, port, "cf-turnstile-response=x");
        v.Result = TurnstileOutcome.Failed; // would fail if it were asked
        Assert.Equal(TurnstileOutcome.Passed, await gate.CheckFormAsync("signin"));
        Assert.Equal(0, v.Calls);
    }

    [Fact]
    public async Task A_request_that_is_not_a_form_is_sent_on_as_missing()
    {
        var (gate, v) = Make(true, 8080); // no form body at all (e.g. a plain beacon without the token)
        v.Result = TurnstileOutcome.Failed;
        Assert.Equal(TurnstileOutcome.Failed, await gate.CheckFormAsync("seen"));
        Assert.Null(v.Token);
    }
}
