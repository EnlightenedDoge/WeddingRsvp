using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>Attendance is split into adults and children: at least one adult, together within the invitation, children stored separately.</summary>
public sealed class AdultsChildrenTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public AdultsChildrenTests()
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
            new Guest { Name = "four", Token = "Fam4Guests", PhoneRaw = "0501111111", PhoneE164 = "+972501111111", InvitedCount = 4 },
            new Guest { Name = "single", Token = "Uno4Guests", PhoneRaw = "0502222222", PhoneE164 = "+972502222222", InvitedCount = 1 },
            new Guest { Name = "open", Token = "NoLimitGst", PhoneRaw = "0503333333", PhoneE164 = "+972503333333", InvitedCount = 0 });
        t.SaveChanges();
    }

    private AppDbContext Db()
    {
        var t = new TenantContext();
        t.Use(_couple);
        return new AppDbContext(_options, t);
    }

    private async Task<RsvpResult> SubmitAsync(string token, bool? attending, int adults, int children)
    {
        var tenant = new TenantContext();
        using var db = new AppDbContext(_options, tenant);
        var svc = new RsvpService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System), tenant);
        return await svc.SubmitAsync(token, attending, adults, children);
    }

    private async Task<Guest> GuestAsync(string token)
    {
        using var db = Db();
        return await db.Guests.SingleAsync(g => g.Token == token);
    }

    [Fact]
    public async Task Adults_and_children_are_stored_separately_and_the_total_includes_both()
    {
        Assert.Equal(RsvpResult.Saved, await SubmitAsync("Fam4Guests", true, 2, 2));
        var g = await GuestAsync("Fam4Guests");
        Assert.Equal(4, g.AttendingCount);       // everyone, so existing totals keep working
        Assert.Equal(2, g.AttendingChildren);
        Assert.Equal(2, g.AttendingAdults);
    }

    [Fact]
    public async Task One_adult_and_no_children_is_the_normal_answer()
    {
        Assert.Equal(RsvpResult.Saved, await SubmitAsync("Fam4Guests", true, 1, 0));
        var g = await GuestAsync("Fam4Guests");
        Assert.Equal((1, 0, 1), (g.AttendingAdults, g.AttendingChildren, g.AttendingCount));
    }

    [Theory]
    [InlineData(0, 1)]   // children alone can't attend
    [InlineData(0, 0)]
    [InlineData(-1, 2)]
    [InlineData(2, -1)]
    [InlineData(3, 2)]   // 5 > the 4 invited
    [InlineData(4, 1)]
    public async Task Invalid_combinations_are_refused_and_change_nothing(int adults, int children)
    {
        Assert.Equal(RsvpResult.InvalidCount, await SubmitAsync("Fam4Guests", true, adults, children));
        var g = await GuestAsync("Fam4Guests");
        Assert.Equal(RsvpStatus.Pending, g.Status);
        Assert.Equal((0, 0), (g.AttendingCount, g.AttendingChildren));
    }

    [Fact]
    public async Task An_invitation_for_one_person_cannot_bring_children()
    {
        Assert.Equal(RsvpResult.InvalidCount, await SubmitAsync("Uno4Guests", true, 1, 1));
        Assert.Equal(RsvpResult.Saved, await SubmitAsync("Uno4Guests", true, 1, 0));
    }

    [Theory]
    [InlineData(2, 97, RsvpResult.Saved)]                 // 99 together is the cap
    [InlineData(2, 98, RsvpResult.InvalidCount)]
    [InlineData(1, 5, RsvpResult.Saved)]
    public async Task With_no_guest_limit_adults_and_children_together_are_capped_at_99(int adults, int children, RsvpResult expected) =>
        Assert.Equal(expected, await SubmitAsync("NoLimitGst", true, adults, children));

    [Fact]
    public async Task Declining_or_changing_the_answer_clears_the_children()
    {
        await SubmitAsync("Fam4Guests", true, 2, 2);
        Assert.Equal(RsvpResult.Saved, await SubmitAsync("Fam4Guests", false, 3, 3)); // numbers sent with a "no" are ignored
        var declined = await GuestAsync("Fam4Guests");
        Assert.Equal((RsvpStatus.Declined, 0, 0), (declined.Status, declined.AttendingCount, declined.AttendingChildren));

        await SubmitAsync("Fam4Guests", true, 1, 2);
        await SubmitAsync("Fam4Guests", true, 2, 0);                                   // changing the answer replaces the children
        var changed = await GuestAsync("Fam4Guests");
        Assert.Equal((2, 0, 2), (changed.AttendingAdults, changed.AttendingChildren, changed.AttendingCount));
    }

    [Fact]
    public void Answers_from_before_children_were_tracked_count_as_adults()
    {
        var old = new Guest { AttendingCount = 3, AttendingChildren = 0 };
        Assert.Equal(3, old.AttendingAdults);
    }

    [Fact]
    public async Task The_dashboard_totals_adults_and_children_separately()
    {
        await SubmitAsync("Fam4Guests", true, 2, 1);
        await SubmitAsync("NoLimitGst", true, 3, 4);
        using var db = Db();
        var stats = await new DashboardService(db).GetAsync();
        Assert.Equal(10, stats.AttendingPeople);       // everyone
        Assert.Equal(5, stats.AttendingAdults);
        Assert.Equal(5, stats.AttendingChildren);
    }

    public void Dispose() => _conn.Dispose();
}
