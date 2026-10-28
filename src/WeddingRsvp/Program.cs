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
builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection("Sms"));
builder.Services.Configure<SendingOptions>(builder.Configuration.GetSection("Sending"));
var appOptions = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions();

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
builder.Services.AddScoped<CampaignService>();
builder.Services.AddScoped<MessageDispatcher>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddHostedService<MessagingWorker>();

var smsProvider = builder.Configuration["Sms:Provider"] ?? "Log";
if (smsProvider.Equals("Sms4Free", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<ISmsProvider, Sms4FreeProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
else if (smsProvider.Equals("Log", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<ISmsProvider, LogSmsProvider>();
else
    throw new InvalidOperationException($"Unknown Sms:Provider '{smsProvider}' (expected Log or Sms4Free).");
builder.Services.AddScoped<ProviderCircuitBreaker>();
builder.Services.AddScoped<DeliveryReports>();
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
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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
    o.AddPolicy("SiteAdmin", p => p.RequireRole(Roles.SiteAdmin));
});
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

// --- Web
builder.Services.AddRazorPages(o =>
    {
        o.Conventions.AuthorizeFolder("/Admin", "Couple");
        o.Conventions.AuthorizeFolder("/SiteAdmin", "SiteAdmin"); // routes are /admin/site/**
    })
    // Empty form fields bind as null; don't treat every non-nullable string as [Required].
    .AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // NPM sits on the Docker network in front of us; the container ports are not published,
    // so trusting the single forwarding hop is safe.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.ForwardLimit = 1;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Connection.LocalPort == appOptions.AdminPort
            ? (HttpMethods.IsPost(ctx.Request.Method) && AdminPortOnly.IsCredentialPath(ctx.Request.Path)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    "cred:" + ctx.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("admin"))
            : RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
});

var app = builder.Build();

// Guest SMS links and set-password links are built from these. The localhost defaults would put dead links in real messages.
if (!app.Environment.IsDevelopment())
{
    foreach (var (name, url) in new[] { ("App__PublicBaseUrl", appOptions.PublicBaseUrl), ("App__AdminBaseUrl", appOptions.AdminBaseUrl) })
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.IsLoopback)
            app.Logger.LogWarning("{Setting} is '{Url}': links built from it will not work for guests. Set it in .env.", name, url);
}

// --- Database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.GetMigrations().Any())
    {
        db.Database.Migrate();
    }
    else
    {
        // Scaffold convenience only. After `dotnet ef migrations add Initial`, delete data/rsvp.db once.
        app.Logger.LogWarning("No EF migrations found; creating schema with EnsureCreated()");
        db.Database.EnsureCreated();
    }
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
    await next();
});

// Admin routes exist only on the admin port. Requests reaching /admin through the public
// proxy host get a bare 404, so the NPM access list in front of admin.* can't be bypassed.
app.Use(async (ctx, next) =>
{
    var onAdminPort = ctx.Connection.LocalPort == appOptions.AdminPort;
    if (AdminPortOnly.Matches(ctx.Request.Path) && !onAdminPort)
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
    sb.AppendLine("שם,טלפון,מוזמנים,סטטוס,מגיעים,הערות");
    foreach (var g in guests)
    {
        var status = g.Status switch
        {
            RsvpStatus.Attending => "מגיעים",
            RsvpStatus.Declined => "לא מגיעים",
            _ => "טרם ענו",
        };
        sb.AppendLine(string.Join(",",
            CsvReader.Escape(g.Name), CsvReader.Escape(g.PhoneRaw), g.InvitedCount,
            status, g.AttendingCount, CsvReader.Escape(g.Notes)));
    }
    // UTF-8 BOM so Excel opens Hebrew correctly.
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", "guests.csv");
}).RequireAuthorization("Couple");

app.Run();

/// <summary>Paths that exist only on the admin port: the admin area, sign-in and account setup.</summary>
internal static class AdminPortOnly
{
    public static bool Matches(PathString p) =>
        p.StartsWithSegments("/admin") || p.StartsWithSegments("/signin") || p.StartsWithSegments("/signout")
        || p.StartsWithSegments("/set-password");

    /// <summary>Endpoints that accept credentials; rate limited per IP.</summary>
    public static bool IsCredentialPath(PathString p) =>
        p.StartsWithSegments("/signin") || p.StartsWithSegments("/set-password");
}

internal static class MediaPaths
{
    public static string Invitation(string dataDir, int coupleId, string file) =>
        Path.Combine(dataDir, "media", coupleId.ToString(), Path.GetFileName(file));
}

