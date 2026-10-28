using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>Couple A must never see or touch couple B's data, through any query or save.</summary>
public sealed class TenantIsolationTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _a, _b;

    public TenantIsolationTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var a = new Couple { DisplayName = "A" };
        var b = new Couple { DisplayName = "B" };
        db.Couples.AddRange(a, b);
        db.SaveChanges();
        _a = a.Id;
        _b = b.Id;

        // Seed each couple's rows through its own tenant scope, the way the app does.
        Seed(_a, "אורח של א", "AaaaaaaaaA", "0501111111");
        Seed(_b, "אורח של ב", "BbbbbbbbbB", "0502222222");
    }

    private AppDbContext As(int? coupleId)
    {
        var t = new TenantContext();
        if (coupleId is { } id) t.Use(id);
        return new AppDbContext(_options, t);
    }

    private void Seed(int coupleId, string name, string token, string phone)
    {
        using var db = As(coupleId);
        var g = new Guest { Name = name, Token = token, PhoneRaw = phone, PhoneE164 = "+972" + phone[1..] };
        db.Guests.Add(g);
        db.SaveChanges();
        db.Messages.Add(new MessageLog { GuestId = g.Id, Kind = MessageKind.Test, ToE164 = g.PhoneE164!, Body = "x" });
        db.EventSettings.Add(new EventSettings { CoupleNames = name });
        db.SaveChanges();
    }

    [Fact]
    public async Task Each_couple_sees_only_their_own_rows()
    {
        using var a = As(_a);
        Assert.Equal("אורח של א", (await a.Guests.SingleAsync()).Name);
        Assert.Single(await a.Messages.ToListAsync());
        Assert.Equal("אורח של א", (await a.EventSettings.SingleAsync()).CoupleNames);

        using var b = As(_b);
        Assert.Equal("אורח של ב", (await b.Guests.SingleAsync()).Name);
    }

    [Fact]
    public async Task With_no_tenant_nothing_is_visible()
    {
        using var none = As(null);
        Assert.Empty(await none.Guests.ToListAsync());
        Assert.Empty(await none.Messages.ToListAsync());
        Assert.Empty(await none.EventSettings.ToListAsync());
    }

    [Fact]
    public async Task Another_couples_guest_cannot_be_loaded_by_id_or_token()
    {
        int bGuestId;
        using (var b = As(_b)) bGuestId = (await b.Guests.SingleAsync()).Id;

        using var a = As(_a);
        Assert.Null(await a.Guests.FindAsync(bGuestId));
        Assert.Null(await a.Guests.FirstOrDefaultAsync(g => g.Token == "BbbbbbbbbB"));
    }

    [Fact]
    public async Task Bulk_delete_only_touches_the_current_couple()
    {
        using (var a = As(_a)) await a.Guests.ExecuteDeleteAsync();
        using var b = As(_b);
        Assert.Single(await b.Guests.ToListAsync());
    }

    [Fact]
    public async Task Writing_a_row_for_another_couple_is_blocked()
    {
        using var a = As(_a);
        a.Guests.Add(new Guest { Name = "x", Token = "ZzzzzzzzzZ", CoupleId = _b });
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task Creating_data_without_a_tenant_is_blocked()
    {
        using var none = As(null);
        none.Guests.Add(new Guest { Name = "x", Token = "YyyyyyyyyY" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.SaveChangesAsync());
    }

    [Fact]
    public async Task Public_token_lookup_binds_the_request_to_that_guests_couple()
    {
        var tenant = new TenantContext();
        using var db = new AppDbContext(_options, tenant);
        var rsvp = new RsvpService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System), tenant);

        var g = await rsvp.FindAsync("BbbbbbbbbB");
        Assert.NotNull(g);
        Assert.Equal(_b, tenant.CoupleId);
        Assert.Equal("אורח של ב", (await db.EventSettings.SingleAsync()).CoupleNames); // now scoped to B
        Assert.Null(await rsvp.FindAsync("not-a-token"));
    }

    [Fact]
    public async Task Planners_queue_messages_only_for_their_own_couple()
    {
        using var db = As(_a);
        var tenant = new TenantContext();
        tenant.Use(_a);
        using var scoped = new AppDbContext(_options, tenant);
        var settings = new SettingsService(scoped, tenant);
        var s = await settings.GetAsync();
        s.CoupleNames = "A";
        s.StartsAtLocal = DateTime.Today.AddMonths(2).AddHours(19);
        await scoped.SaveChangesAsync();

        var campaign = new CampaignService(scoped, settings, new IsraelClock(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new WeddingRsvp.Options.AppOptions()),
            new WeddingRsvp.Services.Sms.LogSmsProvider(NullLogger<WeddingRsvp.Services.Sms.LogSmsProvider>.Instance),
            NullLogger<CampaignService>.Instance);
        Assert.Equal(1, await campaign.QueueInvitesAsync());

        using var b = As(_b);
        Assert.Single(await b.Messages.ToListAsync()); // only B's seeded message, nothing new
    }

    public void Dispose() => _conn.Dispose();
}
