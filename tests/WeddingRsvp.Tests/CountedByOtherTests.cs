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

/// <summary>"Arriving, counted by another guest": answered, adds no people, still thanked; set in bulk by the couple.</summary>
public sealed class CountedByOtherTests : IDisposable
{
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero); // 11:00 Israel
    }

    private static readonly IsraelClock Clock = new(new FixedTime());
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public CountedByOtherTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using (var db = new AppDbContext(_options, new TenantContext()))
        {
            db.Database.EnsureCreated();
            var c = new Couple { DisplayName = "A" };
            db.Couples.Add(c);
            db.SaveChanges();
            _couple = c.Id;
        }
        using var t = Db();
        // The event was two days ago, so the thank-you window (the day after, from the thank-you hour, for two days) is open now.
        t.EventSettings.Add(new EventSettings { CoupleNames = "D & Y", StartsAtLocal = new DateTime(2026, 10, 26, 20, 0, 0), VenueName = "Hall", Title = "T" });
        t.Guests.AddRange(
            Guest("Pend", "1", RsvpStatus.Pending), Guest("Yes", "2", RsvpStatus.Attending, 2), Guest("Plus", "3", RsvpStatus.CountedByOther),
            Guest("No", "4", RsvpStatus.Declined));
        t.SaveChanges();
    }

    private static Guest Guest(string name, string n, RsvpStatus s, int people = 0) => new()
    {
        Name = name, Token = "Token" + n + "AAAA2"[..4] + "x", PhoneRaw = "05000000" + n + n, PhoneE164 = "+9725000000" + n + n, Status = s,
        AttendingCount = people, InvitedCount = 2, InviteSentAtUtc = DateTime.UtcNow.AddDays(-9),
        RespondedAtUtc = s == RsvpStatus.Pending ? null : DateTime.UtcNow.AddDays(-5),
    };

    private AppDbContext Db() { var t = new TenantContext(); t.Use(_couple); return new AppDbContext(_options, t); }

    private (CampaignService Campaign, GuestBulkService Bulk) Services(AppDbContext db)
    {
        var tenant = new TenantContext(); tenant.Use(_couple);
        var campaign = new CampaignService(db, new SettingsService(db, tenant), Clock,
            Microsoft.Extensions.Options.Options.Create(new AppOptions()),
            new LogSmsProvider(NullLogger<LogSmsProvider>.Instance), NullLogger<CampaignService>.Instance);
        return (campaign, new GuestBulkService(db, campaign, new FamilyService(db), Clock));
    }

    [Fact]
    public async Task Thank_you_goes_to_attendees_and_counted_guests_but_not_pending_or_declined()
    {
        using var db = Db();
        await Services(db).Campaign.PlanAsync();
        var thanked = await db.Messages.Where(m => m.Kind == MessageKind.ThankYou).Select(m => m.Guest!.Name).ToListAsync();
        Assert.Equal(new[] { "Plus", "Yes" }, thanked.OrderBy(x => x));
    }

    [Fact]
    public async Task Dashboard_counts_the_guest_as_answered_with_no_people()
    {
        using var db = Db();
        var s = await new DashboardService(db).GetAsync();
        Assert.Equal(1, s.CountedByOther);
        Assert.Equal(2, s.AttendingPeople);        // only "Yes"
        Assert.Equal(1, s.AttendingInvitations);
        Assert.Equal(1, s.Pending);
        Assert.Equal(3, s.Responded);
    }

    [Fact]
    public async Task Bulk_status_sets_the_answer_with_no_people_and_cancels_reminders()
    {
        using var db = Db();
        var pend = db.Guests.Single(g => g.Name == "Pend");
        db.Messages.Add(new MessageLog { GuestId = pend.Id, Kind = MessageKind.Reminder, Status = MessageStatus.Queued, Body = "x", ToE164 = pend.PhoneE164!, NotBeforeUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var (changed, skipped) = await Services(db).Bulk.SetStatusAsync([pend.Id], RsvpStatus.CountedByOther, 1, 0);

        Assert.Equal((1, 0), (changed, skipped));
        using var check = Db();
        var g = check.Guests.Single(x => x.Name == "Pend");
        Assert.Equal((RsvpStatus.CountedByOther, 0, RsvpSource.Manual), (g.Status, g.AttendingCount, g.Source));
        Assert.NotNull(g.RespondedAtUtc);
        Assert.Equal(MessageStatus.Cancelled, check.Messages.Single(m => m.Kind == MessageKind.Reminder).Status);
    }

    [Fact]
    public async Task Bulk_attending_skips_guests_whose_invitation_is_too_small_and_pending_clears_the_answer()
    {
        using var db = Db();
        var (bulk, ids) = (Services(db).Bulk, db.Guests.Where(g => g.Name == "Pend" || g.Name == "Yes").Select(g => g.Id).ToArray());
        var (changed, skipped) = await bulk.SetStatusAsync(ids, RsvpStatus.Attending, 2, 1); // 3 people; invitations cover 2
        Assert.Equal((0, 2), (changed, skipped));

        (changed, _) = await bulk.SetStatusAsync(ids, RsvpStatus.Pending, 1, 0);
        Assert.Equal(2, changed);
        using var check = Db();
        Assert.All(check.Guests.Where(g => ids.Contains(g.Id)), g => Assert.Equal((RsvpStatus.Pending, 0, null), (g.Status, g.AttendingCount, g.RespondedAtUtc)));
    }

    [Fact]
    public async Task Bulk_status_rejects_a_nonsense_headcount()
    {
        using var db = Db();
        var (changed, _) = await Services(db).Bulk.SetStatusAsync([1], RsvpStatus.Attending, 0, 0);
        Assert.Equal(-1, changed);
    }

    public void Dispose() => _conn.Dispose();
}
