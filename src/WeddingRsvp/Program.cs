using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;

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

// --- App services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IsraelClock>();
builder.Services.AddSingleton<SendingWindow>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<RsvpService>();
builder.Services.AddScoped<CampaignService>();
builder.Services.AddScoped<MessageDispatcher>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddHostedService<MessagingWorker>();

var smsProvider = builder.Configuration["Sms:Provider"] ?? "Log";
if (smsProvider.Equals("Http", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<ISmsSender, HttpSmsSender>();
else
    builder.Services.AddSingleton<ISmsSender, LogSmsSender>();

// --- Web
builder.Services.AddRazorPages()
    // Empty form fields bind as null; don't treat every non-nullable string as [Required].
    .AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // NPM sits on the Docker network in front of us; the container ports are not published,
    // so trusting the single forwarding hop is safe.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    o.ForwardLimit = 1;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Connection.LocalPort == appOptions.AdminPort
            ? RateLimitPartition.GetNoLimiter("admin")
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

app.UseForwardedHeaders();

// Admin routes exist only on the admin port. Requests reaching /admin through the public
// proxy host get a bare 404, so the NPM access list in front of admin.* can't be bypassed.
app.Use(async (ctx, next) =>
{
    var onAdminPort = ctx.Connection.LocalPort == appOptions.AdminPort;
    if (ctx.Request.Path.StartsWithSegments("/admin") && !onAdminPort)
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

app.UseRequestLocalization("he-IL");
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

app.MapRazorPages();

// --- Guest-facing endpoints
app.MapGet("/event.ics", async (SettingsService settings, IsraelClock clock, IOptions<AppOptions> o) =>
{
    var s = await settings.GetAsync();
    var ics = CalendarLinks.BuildIcs(s, o.Value.PublicBaseUrl, clock.UtcNow);
    if (ics is null) return Results.NotFound();
    return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "wedding.ics");
});

app.MapGet("/media/invitation", async (SettingsService settings) =>
{
    var s = await settings.GetAsync();
    if (s.InvitationImageFile is null) return Results.NotFound();
    var path = Path.Combine(dataDir, "media", s.InvitationImageFile);
    if (!File.Exists(path)) return Results.NotFound();
    var contentType = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
    return Results.File(path, contentType);
});

app.MapGet("/healthz", () => Results.Text("ok"));

// --- SMS delivery reports
// TODO: adapt the payload parsing to the chosen provider. Configure the provider's callback URL as
// https://rsvp.example.com/webhooks/sms-delivery/{App:DeliveryWebhookSecret}
app.MapPost("/webhooks/sms-delivery/{secret}", async (
    string secret, DeliveryReport report, IOptions<AppOptions> o, AppDbContext db, IsraelClock clock) =>
{
    var expected = o.Value.DeliveryWebhookSecret;
    if (string.IsNullOrEmpty(expected) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected)))
        return Results.NotFound();

    var m = await db.Messages.FirstOrDefaultAsync(x => x.ProviderMessageId == report.MessageId);
    if (m is null) return Results.Ok();

    if (report.Delivered)
    {
        m.Status = MessageStatus.Delivered;
        m.DeliveredAtUtc = clock.UtcNow;
    }
    else
    {
        m.Status = MessageStatus.Failed;
        m.Error = report.Error ?? "Not delivered";
    }
    await db.SaveChangesAsync();
    return Results.Ok();
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
});

app.Run();

/// <summary>Placeholder delivery-report shape; replace with the provider's real payload.</summary>
internal sealed record DeliveryReport(string MessageId, bool Delivered, string? Error);
