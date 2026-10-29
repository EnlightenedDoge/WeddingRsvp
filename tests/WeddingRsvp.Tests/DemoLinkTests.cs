using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>The link in a test message opens the couple's demo page, whoever the phone belongs to.</summary>
public sealed class DemoLinkTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _a, _b;

    public DemoLinkTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var a = new Couple { DisplayName = "A" };
        var b = new Couple { DisplayName = "B" };
        db.Couples.AddRange(a, b);
        db.SaveChanges();
        _a = a.Id; _b = b.Id;
    }

    private (AppDbContext db, TenantContext t) Scope(int? couple)
    {
        var t = new TenantContext();
        if (couple is { } id) t.Use(id);
        return (new AppDbContext(_options, t), t);
    }

    private async Task<string> QueueTestAsync(int couple)
    {
        var (db, t) = Scope(couple);
        using var _ = db;
        var settings = new SettingsService(db, t);
        var s = await settings.GetAsync();
        s.CoupleNames = "X & Y"; s.StartsAtLocal = DateTime.Today.AddMonths(2); s.VenueName = "Hall";
        await db.SaveChangesAsync();
        var campaign = new CampaignService(db, settings, new IsraelClock(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new AppOptions { PublicBaseUrl = "https://x.example" }),
            new LogSmsProvider(NullLogger<LogSmsProvider>.Instance), NullLogger<CampaignService>.Instance);
        Assert.True(await campaign.QueueTestAsync("0501234567", MessageKind.Invite));
        var body = (await db.Messages.OrderByDescending(m => m.Id).FirstAsync()).Body;
        return System.Text.RegularExpressions.Regex.Match(body, @"https://x\.example/([A-Za-z0-9]{10})").Groups[1].Value;
    }

    private RsvpService Rsvp(AppDbContext db, TenantContext t) =>
        new(db, new SettingsService(db, t), new IsraelClock(TimeProvider.System), t);

    [Fact]
    public async Task The_test_message_link_resolves_to_a_demo_guest_of_that_couple_and_stays_the_same()
    {
        var token = await QueueTestAsync(_a);
        Assert.Equal(10, token.Length);
        Assert.Equal(token, await QueueTestAsync(_a)); // one demo token per couple
        Assert.NotEqual(token, await QueueTestAsync(_b));

        var (db, t) = Scope(null);
        using var _ = db;
        var g = await Rsvp(db, t).FindAsync(token);
        Assert.NotNull(g);
        Assert.True(g!.IsDemo);
        Assert.Equal(_a, t.CoupleId); // the request is scoped to the couple that owns the link
    }

    [Fact]
    public async Task Answering_or_opening_the_demo_page_changes_nothing()
    {
        var token = await QueueTestAsync(_a);
        var (db, t) = Scope(null);
        using var _ = db;
        var svc = Rsvp(db, t);
        Assert.Equal(RsvpResult.Saved, await svc.SubmitAsync(token, true, 2, 1));
        Assert.True(await svc.SeenAsync(token));
        Assert.Empty(await db.Guests.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_new_settings_row_gets_its_demo_token_at_once()
    {
        var (db, t) = Scope(_a);
        using var _ = db;
        var s = await new SettingsService(db, t).GetAsync();
        Assert.True(TokenGenerator.LooksValid(s.PreviewToken));
    }

    [Fact]
    public async Task Couples_that_predate_the_token_are_backfilled_with_unique_tokens_once()
    {
        // Settings rows as an older version left them: no token.
        foreach (var id in new[] { _a, _b })
        {
            var (d, _t) = Scope(id);
            using (d) { d.EventSettings.Add(new EventSettings { CoupleId = id }); await d.SaveChangesAsync(); }
        }
        var (db, t) = Scope(null);
        using var _ = db;
        Assert.Equal(2, await PreviewTokens.BackfillAsync(db));
        var tokens = await db.EventSettings.IgnoreQueryFilters().Select(x => x.PreviewToken).ToListAsync();
        Assert.All(tokens, x => Assert.True(TokenGenerator.LooksValid(x)));
        Assert.Equal(2, tokens.Distinct().Count());
        Assert.Equal(0, await PreviewTokens.BackfillAsync(db)); // nothing left to do on the next start
    }

    [Fact]
    public async Task Test_messages_show_unfilled_values_as_placeholders()
    {
        var (db, t) = Scope(_a);
        using var _ = db;
        var settings = new SettingsService(db, t);
        var s = await settings.GetAsync();
        s.InviteTemplate = "{name}, {couple} on {date} at {venue}: {link}";
        await db.SaveChangesAsync();
        var campaign = new CampaignService(db, settings, new IsraelClock(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new AppOptions { PublicBaseUrl = "https://x.example" }),
            new LogSmsProvider(NullLogger<LogSmsProvider>.Instance), NullLogger<CampaignService>.Instance);
        Assert.True(await campaign.QueueTestAsync("0501234567", MessageKind.Invite));
        var body = (await db.Messages.SingleAsync()).Body;
        Assert.Contains("{couple} on {date} at {venue}", body);
        // Real messages are different: they are never built while a value is missing.
        Assert.Equal(",  on  at : l", MessageTemplates.Render("{name}, {couple} on {date} at {venue}: {link}", "", s, "l"));
    }

    [Fact]
    public async Task Random_tokens_are_still_unknown()
    {
        var (db, t) = Scope(null);
        using var _ = db;
        Assert.Null(await Rsvp(db, t).FindAsync("ZzzzzzzzzZ"));
    }

    public void Dispose() => _conn.Dispose();
}
