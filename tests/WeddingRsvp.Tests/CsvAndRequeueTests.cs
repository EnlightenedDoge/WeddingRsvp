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

public sealed class CsvAndRequeueTests : IDisposable
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Dana Cohen", "Dana Cohen")]
    public void Export_text_cells_cannot_start_a_formula(string input, string expected) =>
        Assert.Equal(expected, CsvReader.EscapeText(input));

    [Fact]
    public void Phone_cells_keep_their_plus() => Assert.Equal("+972501234567", CsvReader.Escape("+972501234567"));

    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    [Fact]
    public async Task Requeue_skips_guests_already_queued_and_old_tests()
    {
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using (var seed = new AppDbContext(opts, new TenantContext()))
        {
            seed.Database.EnsureCreated();
            seed.Couples.Add(new Couple { DisplayName = "A" });
            seed.SaveChanges();
        }
        var tenant = new TenantContext();
        tenant.Use(1);
        using var db = new AppDbContext(opts, tenant);
        var g1 = new Guest { Name = "one", Token = "AaaaaaaaaA", PhoneRaw = "0501111111", PhoneE164 = "+972501111111" };
        var g2 = new Guest { Name = "two", Token = "BbbbbbbbbB", PhoneRaw = "0502222222", PhoneE164 = "+972502222222" };
        db.Guests.AddRange(g1, g2);
        db.SaveChanges();
        db.Messages.AddRange(
            new MessageLog { GuestId = g1.Id, Kind = MessageKind.Invite, ToE164 = "+972501111111", Body = "x", Status = MessageStatus.Failed },
            new MessageLog { GuestId = g1.Id, Kind = MessageKind.Invite, ToE164 = "+972501111111", Body = "x", Status = MessageStatus.Queued },
            new MessageLog { GuestId = g2.Id, Kind = MessageKind.Invite, ToE164 = "+972502222222", Body = "x", Status = MessageStatus.Failed },
            new MessageLog { GuestId = g2.Id, Kind = MessageKind.Invite, ToE164 = "+972502222222", Body = "x", Status = MessageStatus.Failed },
            new MessageLog { Kind = MessageKind.Test, ToE164 = "+972503333333", Body = "x", Status = MessageStatus.Failed });
        db.SaveChanges();

        var campaign = new CampaignService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new AppOptions()), new LogSmsProvider(NullLogger<LogSmsProvider>.Instance),
            NullLogger<CampaignService>.Instance);
        Assert.Equal(1, await campaign.RequeueFailedAsync()); // only one of guest two's failures

        Assert.Equal(1, await db.Guests.Where(g => g.Id == g1.Id).SelectMany(g => db.Messages.Where(m => m.GuestId == g.Id && m.Status == MessageStatus.Queued)).CountAsync());
        Assert.Equal(1, await db.Messages.CountAsync(m => m.GuestId == g2.Id && m.Status == MessageStatus.Queued));
        Assert.Equal(MessageStatus.Failed, (await db.Messages.SingleAsync(m => m.Kind == MessageKind.Test)).Status);
    }

    public void Dispose() => _conn.Dispose();
}
