using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Auth;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;
using WeddingRsvp.Tenancy;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // Windows-1255 CSVs from Hebrew Excel

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
// A custom Turnstile verify URL is a test hook: outside Development it is ignored, so a stray value can never receive the secret key.
builder.Services.PostConfigure<AppOptions>(o => { if (!builder.Environment.IsDevelopment()) o.TurnstileVerifyUrl = null; });
builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection("Sms"));
builder.Services.Configure<SendingOptions>(builder.Configuration.GetSection("Sending"));
var appOptions = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions();
if (!builder.Environment.IsDevelopment()) appOptions.TurnstileVerifyUrl = null;

// --- Two ports: public (guest pages) and admin. NPM maps rsvp.* -> PublicPort, admin.* -> AdminPort.
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(appOptions.PublicPort);
    k.ListenAnyIP(appOptions.AdminPort);
});

// --- Storage
var dataDir = Path.GetFullPath(appOptions.DataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "media"));
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataDir, "rsvp.db")}"));

// Keep the data-protection keys on the data volume: sessions, antiforgery and set-password links survive a container recreate.
builder.Services.AddDataProtection()
    .SetApplicationName("WeddingRsvp")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

// --- App services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IsraelClock>();
builder.Services.AddSingleton<SendingWindow>();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Ui>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<CoupleAdminService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<RsvpService>();
builder.Services.AddScoped<GroupLinkService>();
builder.Services.AddScoped<CampaignService>();
builder.Services.AddScoped<MessageDispatcher>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddHostedService<MessagingWorker>();
builder.Services.AddHostedService<BackupCleanupService>();

var smsProvider = builder.Configuration["Sms:Provider"] ?? "Log";
if (smsProvider.Equals("Sms4Free", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<ISmsProvider, Sms4FreeProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
else if (smsProvider.Equals("Log", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<ISmsProvider, LogSmsProvider>();
else
    throw new InvalidOperationException($"Unknown Sms:Provider '{smsProvider}' (expected Log or Sms4Free).");
builder.Services.AddScoped<ProviderCircuitBreaker>();
builder.Services.AddScoped<DeliveryReports>();
builder.Services.AddHttpClient<MapLinkResolver>(c => c.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }); // hops are followed by hand, allow-listed
builder.Services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<TurnstileGate>();
builder.Services.AddScoped<AllocationService>();

// --- Identity: the data here is very sensitive, so accounts are locked down.
builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AppClaimsFactory>()
    .AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(7));
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "wr.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    // Secure follows the request (nginx forwards X-Forwarded-Proto: https). The "HTTPS only" rule is enforced per port in the
    // port middleware, so the private admin port alone can be allowed over plain HTTP (App__AdminAllowPlainHttp).
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
    o.LoginPath = "/signin";
    o.AccessDeniedPath = "/signin";
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Couple", p => p.RequireAssertion(c =>
        c.User.IsInRole(Roles.Couple)
        || (c.User.IsInRole(Roles.SiteAdmin) && c.User.IsActingAsCouple() && c.User.CoupleId() is not null)));
    // Site admin only on the private admin port, even if a request somehow got past the path guard.
    o.AddPolicy("SiteAdmin", p => p.RequireRole(Roles.SiteAdmin)
        .RequireAssertion(c => c.Resource is HttpContext h && h.Connection.LocalPort == appOptions.AdminPort));
});
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SameSite = SameSiteMode.Strict;
    // Secure follows the request (nginx forwards X-Forwarded-Proto: https). The "HTTPS only" rule is enforced per port in the
    // port middleware, so the private admin port alone can be allowed over plain HTTP (App__AdminAllowPlainHttp).
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// --- Web
builder.Services.AddRazorPages(o =>
    {
        o.Conventions.AuthorizeFolder("/Admin", "Couple");
        o.Conventions.AuthorizeFolder("/SiteAdmin", "SiteAdmin"); // routes are /admin/site/**
        // The couple's preview of the guest page: the same page as /{token}, checked for a couple session inside the page.
        o.Conventions.AddPageRoute("/Rsvp", "admin/preview");
        o.Conventions.AddPageRoute("/GroupRsvp", "admin/preview/group");
    })
    // Empty form fields bind as null; don't treat every non-nullable string as [Required].
    .AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Honor X-Forwarded-For/Proto only when the request actually comes from the configured proxy network (NPM on the
    // Docker network by default). A client that reaches a published port directly cannot forge its IP or scheme: the
    // middleware leaves RemoteIpAddress/IsHttps as the real socket values, so the rate limiter, audit log and HTTPS
    // guard all key off something the attacker can't set. ForwardLimit=null walks every trusted hop; an untrusted peer
    // stops the chain. See AppOptions.TrustedProxyNetworks.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.ForwardLimit = null;
    foreach (var net in ParseProxyNetworks(appOptions.TrustedProxyNetworks))
        o.KnownIPNetworks.Add(net);
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // Sign-in and set-password accept credentials on both ports: 10 attempts a minute per IP.
        if (HttpMethods.IsPost(ctx.Request.Method) && PortGuard.IsCredentialPath(ctx.Request.Path))
            return RateLimitPartition.GetFixedWindowLimiter("cred:" + ip,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        // Group-link pages create guests from anonymous visitors: a tighter limit on the posts (a shared venue network still fits).
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/g"))
            return RateLimitPartition.GetFixedWindowLimiter("group:" + ip,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        // The signed-in portal (and everything on the private admin port) is not throttled like public guest pages.
        if (ctx.Connection.LocalPort == appOptions.AdminPort || PortGuard.IsPortalPath(ctx.Request.Path))
            return RateLimitPartition.GetNoLimiter("portal");
        return RateLimitPartition.GetFixedWindowLimiter(ip,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

var app = builder.Build();

// Guest SMS links and couples' set-password links are built from this. The localhost default would put dead links in real messages.
if (!app.Environment.IsDevelopment() && (!Uri.TryCreate(appOptions.PublicBaseUrl, UriKind.Absolute, out var publicUri) || publicUri.IsLoopback))
    app.Logger.LogWarning("App__PublicBaseUrl is '{Url}': links built from it will not work for guests or couples. Set it in .env.", appOptions.PublicBaseUrl);

// Turnstile: on only when both keys are set. One key alone is a misconfiguration: stay off, but say so.
{
    var haveSite = !string.IsNullOrWhiteSpace(appOptions.TurnstileSiteKey);
    var haveSecret = !string.IsNullOrWhiteSpace(appOptions.TurnstileSecretKey);
    if (haveSite != haveSecret)
        app.Logger.LogWarning("Turnstile is OFF: set both App__TurnstileSiteKey and App__TurnstileSecretKey (only {Which} is set).", haveSite ? "the site key" : "the secret key");
    else if (haveSite)
    {
        var hosts = TurnstileVerifier.AllowedHostnames(appOptions);
        app.Logger.LogInformation("Turnstile is ON for the public port; accepted hostnames: {Hosts}", string.Join(", ", hosts));
        if (!string.IsNullOrWhiteSpace(appOptions.TurnstileVerifyUrl))
            app.Logger.LogWarning("App__TurnstileVerifyUrl is set ({Url}): Turnstile tokens are NOT being checked with Cloudflare. For tests only.", appOptions.TurnstileVerifyUrl);
        if (!app.Environment.IsDevelopment() && hosts.Any(h => h is "localhost" or "127.0.0.1" or "::1"))
            app.Logger.LogWarning("Turnstile accepts a loopback hostname in production; remove it from App__TurnstileAllowedHostnames / App__PublicBaseUrl.");
    }
}

// --- Database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.GetMigrations().Any())
    {
        DbUpgrade.Run(db, dataDir, app.Logger);
    }
    else
    {
        // Scaffold convenience only. After `dotnet ef migrations add Initial`, delete data/rsvp.db once.
        app.Logger.LogWarning("No EF migrations found; creating schema with EnsureCreated()");
        db.Database.EnsureCreated();
    }
}

// Couples that existed before the demo link get their token now (idempotent).
using (var scope = app.Services.CreateScope())
{
    var n = await PreviewTokens.BackfillAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    if (n > 0) app.Logger.LogInformation("Gave {Count} existing couples a demo link", n);
}

using (var scope = app.Services.CreateScope())
    await BootstrapAdmin.EnsureAsync(scope.ServiceProvider, appOptions, app.Logger);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    // Defence-in-depth against content injection. Output is already correctly encoded; this restricts where scripts,
    // styles, frames and connections may come from, forbids plugins/object embedding, and blocks this site being framed.
    // Scripts/styles still allow 'unsafe-inline' because several pages use inline <script>/style= blocks; tightening those
    // to per-response nonces is the follow-up hardening step. The allowed origins match the only third parties in use:
    // Cloudflare Turnstile, cdnjs (Leaflet), Google Fonts, OpenStreetMap tiles and the Photon geocoder.
    h["Content-Security-Policy"] =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "img-src 'self' data: https://tile.openstreetmap.org https://cdnjs.cloudflare.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdnjs.cloudflare.com; " +
        "script-src 'self' 'unsafe-inline' https://challenges.cloudflare.com https://cdnjs.cloudflare.com; " +
        "connect-src 'self' https://photon.komoot.io; " +
        "frame-src https://challenges.cloudflare.com";
    await next();
});

// Two ports. The public port serves guest pages and the couples' sign-in and portal (/signin, /admin/**).
// The site-admin pages (/admin/site/**) exist only on the private admin port: on the public port they get a bare 404,
// so exposing the public host to the internet never exposes them. (The SiteAdmin policy and the sign-in page back this up.)
app.Use(async (ctx, next) =>
{
    var onAdminPort = ctx.Connection.LocalPort == appOptions.AdminPort;
    if (PortGuard.RequiresHttps(app.Environment.IsDevelopment(), onAdminPort, appOptions.AdminAllowPlainHttp, ctx.Request.Path, ctx.Request.IsHttps))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest; // sign-in and the portal never run over plain HTTP, except the opt-in admin port
        await ctx.Response.WriteAsync("HTTPS required");
        return;
    }
    if (!onAdminPort && PortGuard.IsSiteAdminPath(ctx.Request.Path))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (onAdminPort && ctx.Request.Path == "/")
    {
        ctx.Response.Redirect("/admin");
        return;
    }
    await next();
});

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("he-IL")
    .AddSupportedCultures("he-IL", "en")
    .AddSupportedUICultures("he-IL", "en"));
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

app.MapRazorPages();

// --- Guest-facing endpoints. No signed-in user here: the guest token picks the couple.
// The guest page reports back here once a person has actually looked at it (see Rsvp.cshtml). This, not the page load, is an "open".
app.MapPost("/{token:length(10)}/seen", async (string token, RsvpService rsvp, TurnstileGate turnstile) =>
{
    // With Turnstile on, an open counts only when Cloudflare confirms the token the invisible widget produced. Lenient: if the check
    // fails or Cloudflare is unreachable the open is simply not counted; the guest and the page are never affected.
    if (await turnstile.CheckFormAsync("seen") == TurnstileOutcome.Passed)
        await rsvp.SeenAsync(token);
    return Results.NoContent();
});

app.MapGet("/{token:length(10)}/event.ics", async (
    string token, RsvpService rsvp, SettingsService settings, IsraelClock clock, IOptions<AppOptions> o) =>
{
    if (await rsvp.FindAsync(token) is null) return Results.NotFound();
    var ics = CalendarLinks.BuildIcs(await settings.GetAsync(), o.Value.PublicBaseUrl, clock.UtcNow);
    if (ics is null) return Results.NotFound();
    return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "wedding.ics");
});

app.MapGet("/{token:length(10)}/invitation", async (string token, string? lang, RsvpService rsvp, SettingsService settings) =>
{
    if (await rsvp.FindAsync(token) is null) return Results.NotFound();
    return InvitationFile(await settings.GetAsync(), lang);
});

app.MapGet("/{token:length(10)}/background", async (string token, RsvpService rsvp, SettingsService settings) =>
{
    if (await rsvp.FindAsync(token) is null) return Results.NotFound();
    var s = await settings.GetAsync();
    return MediaFile(s.CoupleId, s.PageBackgroundImageFile);
});

// Group-link pages (/g/{code}): the same files, resolved from the group code.
app.MapGet("/g/{code:length(6)}/event.ics", async (
    string code, GroupLinkService groups, SettingsService settings, IsraelClock clock, IOptions<AppOptions> o) =>
{
    if (await groups.FindAsync(code) is null) return Results.NotFound();
    var ics = CalendarLinks.BuildIcs(await settings.GetAsync(), o.Value.PublicBaseUrl, clock.UtcNow);
    return ics is null ? Results.NotFound() : Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "wedding.ics");
});
app.MapGet("/g/{code:length(6)}/invitation", async (string code, string? lang, GroupLinkService groups, SettingsService settings) =>
{
    if (await groups.FindAsync(code) is null) return Results.NotFound();
    return InvitationFile(await settings.GetAsync(), lang);
});
app.MapGet("/g/{code:length(6)}/background", async (string code, GroupLinkService groups, SettingsService settings) =>
{
    if (await groups.FindAsync(code) is null) return Results.NotFound();
    var s = await settings.GetAsync();
    return MediaFile(s.CoupleId, s.PageBackgroundImageFile);
});

// The couple's preview of the guest page (/admin/preview): the same files guests get, without a guest token.
app.MapGet("/admin/preview/invitation", async (string? lang, SettingsService settings) =>
    InvitationFile(await settings.GetAsync(), lang)).RequireAuthorization("Couple");
app.MapGet("/admin/preview/background", async (SettingsService settings) =>
{
    var s = await settings.GetAsync();
    return MediaFile(s.CoupleId, s.PageBackgroundImageFile);
}).RequireAuthorization("Couple");
app.MapGet("/admin/preview/event.ics", async (SettingsService settings, IsraelClock clock, IOptions<AppOptions> o) =>
{
    var ics = CalendarLinks.BuildIcs(await settings.GetAsync(), o.Value.PublicBaseUrl, clock.UtcNow);
    return ics is null ? Results.NotFound() : Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "wedding.ics");
}).RequireAuthorization("Couple");

// The couple's own preview of their uploaded image.
app.MapGet("/admin/media/invitation", async (string? lang, SettingsService settings) =>
    InvitationFile(await settings.GetAsync(), lang, exact: true)).RequireAuthorization("Couple");
app.MapGet("/admin/media/background", async (SettingsService settings) =>
{
    var s = await settings.GetAsync();
    return MediaFile(s.CoupleId, s.PageBackgroundImageFile);
}).RequireAuthorization("Couple");

// The image for the requested language; for guests, a missing one falls back to the other language.
IResult InvitationFile(EventSettings s, string? lang, bool exact = false)
{
    var he = s.InvitationImageFile;
    var en = s.InvitationImageFileEn;
    var file = lang == "en" ? (exact ? en : en ?? he) : (exact ? he : he ?? en);
    return MediaFile(s.CoupleId, file);
}

IResult MediaFile(int coupleId, string? file)
{
    if (file is null) return Results.NotFound();
    var path = MediaPaths.Invitation(dataDir, coupleId, file);
    if (!File.Exists(path)) return Results.NotFound();
    var contentType = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
    return Results.File(path, contentType);
}

app.MapGet("/lang/{code}", (string code, string? returnUrl, HttpContext ctx) =>
{
    var culture = code == "en" ? "en" : "he-IL";
    ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, SameSite = SameSiteMode.Lax,
                            IsEssential = true });
    return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        || returnUrl.StartsWith("//") ? "/" : returnUrl);
});

app.MapGet("/healthz", () => Results.Text("ok"));

// --- SMS delivery reports (SMS4FREE: POST application/x-www-form-urlencoded with to, status, msgId)
// Configure by setting App__DeliveryWebhookSecret; the provider then gets https://<public host>/webhooks/sms-delivery/<secret>
// as returnUrl on every send. The form is read by hand (no [FromForm]) so the provider's POST isn't subject to antiforgery.
app.MapPost("/webhooks/sms-delivery/{secret}", async (
    string secret, HttpContext ctx, IOptions<AppOptions> o, DeliveryReports reports) =>
{
    var expected = o.Value.DeliveryWebhookSecret;
    if (string.IsNullOrEmpty(expected) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected)))
        return Results.NotFound();

    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    await reports.ApplyAsync(form["msgId"], form["to"], form["status"], ctx.RequestAborted);
    return Results.Ok(); // always 200 so the provider doesn't retry or learn which ids exist
});

// --- Admin endpoints (port-guarded by the /admin prefix)
app.MapGet("/admin/export.csv", async (AppDbContext db) =>
{
    var guests = await db.Guests.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
    var sb = new StringBuilder();
    sb.AppendLine("שם,טלפון,מוזמנים,סטטוס,מגיעים,מבוגרים,ילדים,הערות");
    foreach (var g in guests)
    {
        var status = g.Status switch
        {
            RsvpStatus.Attending => "מגיעים",
            RsvpStatus.Declined => "לא מגיעים",
            _ => "טרם ענו",
        };
        sb.AppendLine(string.Join(",",
            CsvReader.EscapeText(g.Name), CsvReader.Escape(g.PhoneRaw), g.NoGuestLimit ? CsvReader.Escape("ללא הגבלה") : g.InvitedCount.ToString(),
            status, g.AttendingCount, g.AttendingAdults, g.AttendingChildren, CsvReader.EscapeText(g.Notes)));
    }
    // UTF-8 BOM so Excel opens Hebrew correctly.
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", "guests.csv");
}).RequireAuthorization("Couple");

app.Run();

partial class Program
{
    /// <summary>Parse a comma-separated list of CIDR networks into <see cref="System.Net.IPNetwork"/>, skipping anything unparseable.</summary>
    internal static IEnumerable<System.Net.IPNetwork> ParseProxyNetworks(string? csv)
    {
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (System.Net.IPNetwork.TryParse(part, out var net))
                yield return net;
    }
}

/// <summary>Which paths are served on which port.</summary>
public static class PortGuard
{
    /// <summary>Site-admin pages: only on the private admin port.</summary>
    public static bool IsSiteAdminPath(PathString p) => p.StartsWithSegments("/admin/site");

    /// <summary>The couples' sign-in and signed-in portal (served on both ports).</summary>
    public static bool IsPortalPath(PathString p) =>
        p.StartsWithSegments("/admin") || p.StartsWithSegments("/signin") || p.StartsWithSegments("/signout")
        || p.StartsWithSegments("/set-password");

    /// <summary>Endpoints that accept credentials; rate limited per IP.</summary>
    public static bool IsCredentialPath(PathString p) =>
        p.StartsWithSegments("/signin") || p.StartsWithSegments("/set-password");

    /// <summary>
    /// Sign-in and the portal need HTTPS (as forwarded by the proxy) outside Development. The only exception is the private
    /// admin port when <c>App__AdminAllowPlainHttp</c> is on, for a LAN-only site admin without a certificate.
    /// </summary>
    public static bool RequiresHttps(bool development, bool onAdminPort, bool adminAllowPlainHttp, PathString path, bool isHttps) =>
        !development && !isHttps && IsPortalPath(path) && !(onAdminPort && adminAllowPlainHttp);
}

internal static class MediaPaths
{
    public static string Invitation(string dataDir, int coupleId, string file) =>
        Path.Combine(dataDir, "media", coupleId.ToString(), Path.GetFileName(file));
}

