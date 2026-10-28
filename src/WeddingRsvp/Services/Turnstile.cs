using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services;

/// <summary>Passed = Cloudflare confirmed the token. Failed = missing, invalid, spent, or for the wrong action or host. Unavailable = we could not reach Cloudflare.</summary>
public enum TurnstileOutcome { Passed, Failed, Unavailable }

public interface ITurnstileVerifier
{
    /// <summary>True only when both the site key and the secret key are configured.</summary>
    bool Enabled { get; }
    string? SiteKey { get; }
    Task<TurnstileOutcome> VerifyAsync(string? token, string action, string? remoteIp, CancellationToken ct = default);
}

/// <summary>
/// Server-side check of a Turnstile token against Cloudflare's siteverify endpoint. A token is single-use. Success also requires the
/// action we asked the widget for and a hostname on the allow-list, so a token minted for another page or site is refused.
/// The secret and the tokens are never logged.
/// </summary>
public sealed class TurnstileVerifier(HttpClient http, IOptions<AppOptions> options, ILogger<TurnstileVerifier> log) : ITurnstileVerifier
{
    public const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private AppOptions O => options.Value;

    public bool Enabled => !string.IsNullOrWhiteSpace(O.TurnstileSiteKey) && !string.IsNullOrWhiteSpace(O.TurnstileSecretKey);
    public string? SiteKey => Enabled ? O.TurnstileSiteKey!.Trim() : null;

    public async Task<TurnstileOutcome> VerifyAsync(string? token, string action, string? remoteIp, CancellationToken ct = default)
    {
        if (!Enabled) return TurnstileOutcome.Passed; // off: nothing to verify
        if (string.IsNullOrWhiteSpace(token)) return TurnstileOutcome.Failed;

        var form = new Dictionary<string, string>
        {
            ["secret"] = O.TurnstileSecretKey!.Trim(),
            ["response"] = token,
        };
        if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;

        Reply? reply;
        try
        {
            using var res = await http.PostAsync(string.IsNullOrWhiteSpace(O.TurnstileVerifyUrl) ? VerifyUrl : O.TurnstileVerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Turnstile siteverify answered HTTP {Status}", (int)res.StatusCode);
                return TurnstileOutcome.Unavailable;
            }
            reply = await res.Content.ReadFromJsonAsync<Reply>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogWarning("Turnstile siteverify unreachable: {Error}", ex.GetType().Name);
            return TurnstileOutcome.Unavailable;
        }

        if (reply is null) return TurnstileOutcome.Unavailable;
        if (!reply.Success)
        {
            var codes = reply.ErrorCodes is { Length: > 0 } c ? string.Join(",", c) : "none";
            // invalid-input-secret here means a wrong or missing secret key: an operator problem, so make it loud.
            if (reply.ErrorCodes?.Contains("invalid-input-secret") == true)
                log.LogError("Turnstile rejected our secret key (invalid-input-secret). Check App__TurnstileSecretKey.");
            else log.LogInformation("Turnstile token refused ({Codes})", codes);
            return TurnstileOutcome.Failed;
        }
        if (!string.Equals(reply.Action, action, StringComparison.Ordinal))
        {
            log.LogWarning("Turnstile token was minted for action '{Got}', expected '{Want}'", reply.Action, action);
            return TurnstileOutcome.Failed;
        }
        if (!HostnameAllowed(reply.Hostname))
        {
            log.LogWarning("Turnstile token was minted for hostname '{Host}', which is not allowed", reply.Hostname);
            return TurnstileOutcome.Failed;
        }
        return TurnstileOutcome.Passed;
    }

    private bool HostnameAllowed(string? hostname) =>
        !string.IsNullOrWhiteSpace(hostname) && AllowedHostnames(O).Contains(hostname.Trim().ToLowerInvariant());

    /// <summary>The configured allow-list, or the host of the public base URL.</summary>
    public static HashSet<string> AllowedHostnames(AppOptions o)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in (o.TurnstileAllowedHostnames ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(h.ToLowerInvariant());
        if (set.Count == 0 && Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var u)) set.Add(u.Host.ToLowerInvariant());
        return set;
    }

    private sealed record Reply(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}

/// <summary>
/// Where Turnstile applies for the current request. It is active only on the public port: the private admin port (site admin, LAN or
/// VPN) never depends on Cloudflare. Pages ask <see cref="Active"/> to decide whether to render the widget, and call
/// <see cref="CheckFormAsync"/> to verify the token the widget put in the form.
/// </summary>
public sealed class TurnstileGate(ITurnstileVerifier verifier, IOptions<AppOptions> options, IHttpContextAccessor http)
{
    public const string FieldName = "cf-turnstile-response";

    public bool Active => verifier.Enabled && http.HttpContext is { } c && c.Connection.LocalPort != options.Value.AdminPort;
    public string SiteKey => verifier.SiteKey ?? "";

    public async Task<TurnstileOutcome> CheckFormAsync(string action, CancellationToken ct = default)
    {
        if (!Active) return TurnstileOutcome.Passed;
        var ctx = http.HttpContext!;
        string? token = null;
        if (ctx.Request.HasFormContentType)
            token = (await ctx.Request.ReadFormAsync(ct))[FieldName].ToString();
        return await verifier.VerifyAsync(token, action, ctx.Connection.RemoteIpAddress?.ToString(), ct);
    }
}
