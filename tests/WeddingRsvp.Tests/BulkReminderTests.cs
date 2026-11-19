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

/// <summary>The Guests list "Send a reminder" bulk action: invited, unanswered guests only; the automatic interval and cap are bypassed.</summary>
public sealed class BulkReminderTests : IDisposable
{
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
    }

    private static readonly IsraelClock Clock = new(new FixedTime());
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public BulkReminderTests()
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
        t.EventSettings.Add(new EventSettings { CoupleNames = "D & Y", StartsAtLocal = new DateTime(2027, 6, 1, 20, 0, 0), VenueName = "Hall", Title = "T" });
        var invited = DateTime.UtcNow.AddHours(-1); // too recent for the automatic reminder, on purpose
        t.Guests.AddRange(
            G("Invited", "1", invited), G("Never", "2", null), G("Answered", "3", invited, RsvpStatus.Attending),
            G("Counted", "4", invited, RsvpStatus.CountedByOther), G("Capped", "5", invited, remindersSent: 99),
            new Guest { Name = "NoPhone", Token = "TokenNoPh2", PhoneRaw = "bad", PhoneError = "invalid", InviteSentAtUtc = invited },
            new Guest { Name = "Group", Token = "TokenGrp22", PhoneRaw = "0509999999", PhoneE164 = "+972509999999", InviteSentAtUtc = invited, GroupLinkId = 1 });
        t.SaveChanges();
    }

    private static Guest G(string name, string n, DateTime? invited, RsvpStatus s = RsvpStatus.Pending, int remindersSent = 0) => new()
    {
        Name = name, Token = "Token" + n + "BBBB2x", PhoneRaw = "05011100" + n + n, PhoneE164 = "+9725011100" + n + n, Status = s,
        InviteSentAtUtc = invited, RemindersSent = remindersSent,
    };

    private AppDbContext Db() { var t = new TenantContext(); t.Use(_couple); return new AppDbContext(_options, t); }

    private GuestBulkService Bulk(AppDbContext db)
    {
        var tenant = new TenantContext(); tenant.Use(_couple);
        var campaign = new CampaignService(db, new SettingsService(db, tenant), Clock,
            Microsoft.Extensions.Options.Options.Create(new AppOptions()),
            new LogSmsProvider(NullLogger<LogSmsProvider>.Instance), NullLogger<CampaignService>.Instance);
        return new GuestBulkService(db, campaign, new FamilyService(db), Clock);
    }

    [Fact]
    public async Task Reminds_invited_unanswered_guests_ignoring_interval_and_cap_and_skips_the_rest()
    {
        using var db = Db();
        var ids = db.Guests.Select(g => g.Id).ToArray();
        var r = await Bulk(db).SendRemindersAsync(ids);
        Assert.Equal((2, 1, 2, 1, 1), (r.Queued, r.NotInvited, r.Answered, r.NoPhone, r.GroupLink)); // Invited + Capped
        var to = await db.Messages.Where(m => m.Kind == MessageKind.Reminder && m.Status == MessageStatus.Queued).Select(m => m.Guest!.Name).ToListAsync();
        Assert.Equal(new[] { "Capped", "Invited" }, to.OrderBy(x => x));
    }

    [Fact]
    public async Task A_second_click_does_not_queue_a_second_reminder()
    {
        using var db = Db();
        var ids = db.Guests.Where(g => g.Name == "Invited").Select(g => g.Id).ToArray();
        await Bulk(db).SendRemindersAsync(ids);
        await Bulk(db).SendRemindersAsync(ids);
        Assert.Equal(1, await db.Messages.CountAsync(m => m.Kind == MessageKind.Reminder));
    }

    [Fact]
    public async Task Nothing_is_queued_while_a_reminder_field_is_missing()
    {
        using var db = Db();
        db.EventSettings.Single().CoupleNames = ""; // {couple} is used by the reminder template
        await db.SaveChangesAsync();
        var r = await Bulk(db).SendRemindersAsync(db.Guests.Select(g => g.Id).ToArray());
        Assert.Equal(0, r.Queued);
        Assert.NotEmpty(r.MissingFields);
    }

    public void Dispose() => _conn.Dispose();
}
