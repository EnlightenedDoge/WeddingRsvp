using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>An invitation with InvitedCount 0 has no guest limit: any headcount up to the cap is accepted.</summary>
public sealed class NoGuestLimitTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public NoGuestLimitTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var c = new Couple { DisplayName = "A" };
        db.Couples.Add(c);
        db.SaveChanges();
        _couple = c.Id;

        using var t = Db();
        t.EventSettings.Add(new EventSettings { StartsAtLocal = DateTime.Today.AddMonths(2) }); // RSVP open
        t.Guests.AddRange(
            new Guest { Name = "open", Token = "NoLimitGst", PhoneRaw = "0501111111", PhoneE164 = "+972501111111", InvitedCount = 0 },
            new Guest { Name = "capped", Token = "CappedGst2", PhoneRaw = "0502222222", PhoneE164 = "+972502222222", InvitedCount = 3 });
        t.SaveChanges();
    }

    private AppDbContext Db()
    {
        var t = new TenantContext();
        t.Use(_couple);
        return new AppDbContext(_options, t);
    }

    private async Task<RsvpResult> SubmitAsync(string token, int count)
    {
        var tenant = new TenantContext(); // the public flow starts with no tenant; the token decides it
        using var db = new AppDbContext(_options, tenant);
        var svc = new RsvpService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System), tenant);
        return await svc.SubmitAsync(token, true, count);
    }

    [Fact]
    public void Zero_means_no_limit_and_is_capped_for_answers()
    {
        var g = new Guest { InvitedCount = 0 };
        Assert.True(g.NoGuestLimit);
        Assert.Equal(Guest.UnlimitedCap, g.MaxHeadcount);
        var limited = new Guest { InvitedCount = 4 };
        Assert.False(limited.NoGuestLimit);
        Assert.Equal(4, limited.MaxHeadcount);
    }

    [Theory]
    [InlineData(1, RsvpResult.Saved)]
    [InlineData(7, RsvpResult.Saved)]
    [InlineData(Guest.UnlimitedCap, RsvpResult.Saved)]
    [InlineData(Guest.UnlimitedCap + 1, RsvpResult.InvalidCount)]
    [InlineData(0, RsvpResult.InvalidCount)]
    public async Task A_guest_with_no_limit_may_name_any_headcount_up_to_the_cap(int count, RsvpResult expected) =>
        Assert.Equal(expected, await SubmitAsync("NoLimitGst", count));

    [Fact]
    public async Task A_guest_with_a_limit_is_still_capped_by_it()
    {
        Assert.Equal(RsvpResult.Saved, await SubmitAsync("CappedGst2", 3));
        Assert.Equal(RsvpResult.InvalidCount, await SubmitAsync("CappedGst2", 4));
    }

    [Fact]
    public async Task The_dashboard_counts_unlimited_invitations_separately_from_the_people_total()
    {
        using var db = Db();
        var stats = await new DashboardService(db).GetAsync();
        Assert.Equal(3, stats.InvitedPeople);          // only the capped invitation adds people
        Assert.Equal(1, stats.UnlimitedInvitations);
        Assert.Equal(2, stats.Invitations);
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData(" 2 ", 2)]
    [InlineData("0", 0)]
    [InlineData("ללא הגבלה", 0)]
    [InlineData("Unlimited", 0)]
    [InlineData("no limit", 0)]
    [InlineData("∞", 0)]
    [InlineData("", 1)]
    [InlineData(null, 1)]
    [InlineData("-3", 1)]
    [InlineData("a few", 1)]
    public void The_import_cell_maps_to_a_limit_or_no_limit(string? cell, int expected) =>
        Assert.Equal(expected, ImportService.ParseInvitedCount(cell));

    public void Dispose() => _conn.Dispose();
}
