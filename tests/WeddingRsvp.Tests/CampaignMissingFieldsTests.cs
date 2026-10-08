using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

/// <summary>The rule "nothing is queued while a field the template uses is unset", against a real SQLite DB.</summary>
public sealed class CampaignMissingFieldsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly CampaignService _campaign;
    private readonly SettingsService _settings;

    public CampaignMissingFieldsTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _settings = new SettingsService(_db);
        var clock = new IsraelClock(TimeProvider.System);
        _campaign = new CampaignService(_db, _settings, clock, Microsoft.Extensions.Options.Options.Create(new AppOptions()),
            NullLogger<CampaignService>.Instance);

        _db.Guests.Add(new Guest { Name = "אורח", PhoneRaw = "050-1234567", PhoneE164 = "+972501234567", Token = "AbCdEfGhJk" });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Invites_are_not_queued_until_couple_and_date_are_set()
    {
        Assert.Equal(0, await _campaign.QueueInvitesAsync());
        Assert.Equal(new[] { "{couple}", "{date}" }, await _campaign.MissingFieldsAsync(MessageKind.Invite));
        Assert.Equal(0, await _db.Messages.CountAsync());

        var s = await _settings.GetAsync();
        s.CoupleNames = "A ו-B";
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _campaign.QueueInvitesAsync()); // date still missing

        s.StartsAtLocal = DateTime.Today.AddMonths(2).AddHours(19);
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _campaign.QueueInvitesAsync());
        var body = (await _db.Messages.SingleAsync()).Body;
        Assert.DoesNotContain("{", body);
        Assert.Contains("A ו-B", body);
    }

    [Fact]
    public async Task Single_invite_is_refused_while_fields_are_missing()
    {
        var id = (await _db.Guests.SingleAsync()).Id;
        Assert.False(await _campaign.QueueInviteAsync(id));
        Assert.Equal(0, await _db.Messages.CountAsync());
    }

    [Fact]
    public async Task Auto_invite_planner_does_nothing_without_an_event_date()
    {
        var s = await _settings.GetAsync();
        s.AutoInviteAtLocal = DateTime.Today.AddDays(-1);
        s.CoupleNames = "A ו-B";
        await _db.SaveChangesAsync();

        await _campaign.PlanAsync();
        Assert.Equal(0, await _db.Messages.CountAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
