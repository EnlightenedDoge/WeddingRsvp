using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>Families: members share one answer, are greeted by the family name, and can be imported by identifier (names may repeat).</summary>
public sealed class FamilyTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public FamilyTests()
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
        t.EventSettings.Add(new EventSettings { StartsAtLocal = DateTime.Today.AddMonths(2), AskDietary = true });
        t.SaveChanges();
    }

    private AppDbContext Db() { var t = new TenantContext(); t.Use(_couple); return new AppDbContext(_options, t); }

    private async Task<ImportReport> ImportAsync(string csv)
    {
        using var db = Db();
        var svc = new ImportService(db, new IsraelClock(TimeProvider.System), new FamilyService(db));
        return await svc.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
    }

    private async Task<RsvpResult> SubmitAsync(string token, bool? attending, int adults, int children, DietaryAnswer? diet = null)
    {
        var tenant = new TenantContext();
        using var db = new AppDbContext(_options, tenant);
        var svc = new RsvpService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System), tenant);
        return await svc.SubmitAsync(token, attending, adults, children, diet);
    }

    private async Task<List<Guest>> GuestsAsync() { using var db = Db(); return await db.Guests.Include(g => g.Family).OrderBy(g => g.Name).ToListAsync(); }

    private const string Csv = "name,phone,invited,family name,family identifier\n" +
        "Dan,0501111111,2,Cohen,A1\nDina,0502222222,2,Cohen,A1\nDov,0503333333,2,Cohen,B7\nSolo,0504444444,1,,\n";

    [Fact]
    public async Task Import_groups_by_identifier_even_when_two_families_share_a_name()
    {
        var report = await ImportAsync(Csv);
        Assert.Equal(4, report.Added);
        var g = await GuestsAsync();
        Assert.Equal(g.Single(x => x.Name == "Dan").FamilyId, g.Single(x => x.Name == "Dina").FamilyId);
        Assert.NotEqual(g.Single(x => x.Name == "Dan").FamilyId, g.Single(x => x.Name == "Dov").FamilyId);
        Assert.Null(g.Single(x => x.Name == "Solo").FamilyId);
        using var db = Db();
        Assert.Equal(2, await db.Families.CountAsync(f => f.Name == "Cohen"));
    }

    [Fact]
    public async Task Import_by_name_only_groups_rows_with_the_same_name()
    {
        await ImportAsync("name,phone,family\nAva,0501111111,Levi\nAmi,0502222222,Levi\nOz,0503333333,Bar\n");
        var g = await GuestsAsync();
        Assert.Equal(g.Single(x => x.Name == "Ava").FamilyId, g.Single(x => x.Name == "Ami").FamilyId);
        Assert.NotEqual(g.Single(x => x.Name == "Ava").FamilyId, g.Single(x => x.Name == "Oz").FamilyId);
    }

    [Fact]
    public async Task One_members_answer_is_every_members_answer_and_counts_once()
    {
        await ImportAsync(Csv);
        var dan = (await GuestsAsync()).Single(x => x.Name == "Dan");
        Assert.Equal(RsvpResult.Saved, await SubmitAsync(dan.Token, true, 3, 1, new DietaryAnswer(1, 0, 0, "x"))); // family of 2+2 = 4 people
        var g = await GuestsAsync();
        foreach (var m in g.Where(x => x.Name is "Dan" or "Dina"))
        {
            Assert.Equal(RsvpStatus.Attending, m.Status);
            Assert.Equal((4, 1, 1), (m.AttendingCount, m.AttendingChildren, m.DietVegetarian));
        }
        Assert.Equal(RsvpStatus.Pending, g.Single(x => x.Name == "Dov").Status); // the other Cohen family is separate
        using var db = Db();
        var stats = await new DashboardService(db).GetAsync();
        Assert.Equal(4, stats.AttendingPeople);
        Assert.Equal(1, stats.DietVegetarian);
    }

    [Fact]
    public async Task The_limit_is_the_whole_familys_and_a_reply_from_one_cancels_everyones_reminders()
    {
        await ImportAsync(Csv);
        var dan = (await GuestsAsync()).Single(x => x.Name == "Dan");
        Assert.Equal(RsvpResult.InvalidCount, await SubmitAsync(dan.Token, true, 4, 1)); // 5 > 2 + 2
        using (var db = Db())
        {
            var dina = await db.Guests.SingleAsync(x => x.Name == "Dina");
            db.Messages.Add(new MessageLog { GuestId = dina.Id, Kind = MessageKind.Reminder, Status = MessageStatus.Queued, ToE164 = "+972502222222", Body = "r" });
            await db.SaveChangesAsync();
        }
        await SubmitAsync(dan.Token, false, 1, 0);
        using var check = Db();
        Assert.Equal(MessageStatus.Cancelled, (await check.Messages.SingleAsync()).Status);
    }

    [Fact]
    public async Task Joining_an_answered_family_adopts_its_answer_and_greeting_uses_the_family_name()
    {
        await ImportAsync(Csv);
        var dan = (await GuestsAsync()).Single(x => x.Name == "Dan");
        await SubmitAsync(dan.Token, true, 2, 0);
        await ImportAsync("name,phone,family id\nDor,0505555555,A1\n");
        var dor = (await GuestsAsync()).Single(x => x.Name == "Dor");
        Assert.Equal(RsvpStatus.Attending, dor.Status);
        Assert.Equal("משפחת Cohen", dor.GreetingName(true));
        Assert.Equal("Cohen family", dor.GreetingName(false));
        Assert.Equal("Solo", (await GuestsAsync()).Single(x => x.Name == "Solo").GreetingName(true));
    }

    [Fact]
    public void Sms_greeting_uses_the_family_name_in_each_language()
    {
        var s = new EventSettings { LanguageMode = LanguageMode.Both, InviteTemplate = "שלום {name}", InviteTemplateEn = "Hello {name}" };
        Assert.Equal("שלום משפחת Cohen\n\nHello Cohen family", MessageTemplates.Body(s, MessageKind.Invite, "Dan", "l", familyName: "Cohen"));
        Assert.Equal("שלום Dan\n\nHello Dan", MessageTemplates.Body(s, MessageKind.Invite, "Dan", "l"));
    }

    [Fact]
    public async Task Couples_quick_edit_changes_headcount_and_diet_for_the_whole_family()
    {
        await ImportAsync(Csv);
        var dan = (await GuestsAsync()).Single(x => x.Name == "Dan");
        using (var db = Db())
        {
            var svc = new FamilyService(db);
            Assert.Equal(AttendanceEdit.NotAttending, await svc.EditAttendanceAsync(dan.Id, 2, 0, new DietaryAnswer(0, 0, 0, null), DateTime.UtcNow));
        }
        await SubmitAsync(dan.Token, true, 2, 0);
        using (var db = Db())
        {
            var svc = new FamilyService(db);
            Assert.Equal(AttendanceEdit.InvalidCount, await svc.EditAttendanceAsync(dan.Id, 4, 1, new DietaryAnswer(0, 0, 0, null), DateTime.UtcNow)); // 5 > 2 + 2
            Assert.Equal(AttendanceEdit.InvalidDiet, await svc.EditAttendanceAsync(dan.Id, 3, 0, new DietaryAnswer(4, 0, 0, null), DateTime.UtcNow));
            Assert.Equal(AttendanceEdit.Saved, await svc.EditAttendanceAsync(dan.Id, 3, 1, new DietaryAnswer(1, 1, 0, " no nuts "), DateTime.UtcNow));
        }
        foreach (var m in (await GuestsAsync()).Where(x => x.Name is "Dan" or "Dina"))
            Assert.Equal((4, 1, 1, 1, "no nuts", RsvpSource.Manual), (m.AttendingCount, m.AttendingChildren, m.DietVegetarian, m.DietVegan, m.DietNote, m.Source));
    }

    public void Dispose() => _conn.Dispose();
}

public sealed class FamilyTabTests
{
    [Fact]
    public async Task Families_tab_lists_one_row_per_family_with_all_members()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WeddingRsvp.Data.AppDbContext>().UseSqlite(conn).Options;
        using var setup = new WeddingRsvp.Data.AppDbContext(options, new WeddingRsvp.Tenancy.TenantContext());
        setup.Database.EnsureCreated();
        var c = new WeddingRsvp.Domain.Couple { DisplayName = "A" };
        setup.Couples.Add(c);
        setup.SaveChanges();
        var tenant = new WeddingRsvp.Tenancy.TenantContext(); tenant.Use(c.Id);
        using var db = new WeddingRsvp.Data.AppDbContext(options, tenant);
        var fam = new WeddingRsvp.Domain.Family { Name = "Cohen", ExternalId = "A1" };
        db.Guests.AddRange(
            new WeddingRsvp.Domain.Guest { Name = "Dan", Token = "TokenAAAA2", Family = fam },
            new WeddingRsvp.Domain.Guest { Name = "Dina", Token = "TokenBBBB2", Family = fam },
            new WeddingRsvp.Domain.Guest { Name = "Solo", Token = "TokenCCCC2" });
        db.SaveChanges();

        var page = new WeddingRsvp.Pages.Admin.Guests.IndexModel(db, new WeddingRsvp.Services.Ui(), new FamilyService(db), new IsraelClock(TimeProvider.System), null!) { View = "families" };
        await page.OnGetAsync();
        var row = Assert.Single(page.Families);
        Assert.Equal(new[] { "Dan", "Dina" }, row.Members.Select(m => m.Name).ToArray());
    }
}

public sealed class BulkGuestTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn = new("Data Source=:memory:");
    private readonly Microsoft.EntityFrameworkCore.DbContextOptions<WeddingRsvp.Data.AppDbContext> _options;
    private readonly int _couple;

    public BulkGuestTests()
    {
        _conn.Open();
        _options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WeddingRsvp.Data.AppDbContext>().UseSqlite(_conn).Options;
        using var db = new WeddingRsvp.Data.AppDbContext(_options, new WeddingRsvp.Tenancy.TenantContext());
        db.Database.EnsureCreated();
        var c = new WeddingRsvp.Domain.Couple { DisplayName = "A" };
        db.Couples.Add(c);
        db.SaveChanges();
        _couple = c.Id;
        using var t = Db();
        t.EventSettings.Add(new EventSettings { CoupleNames = "D & Y", StartsAtLocal = DateTime.Today.AddMonths(2), VenueName = "Hall", Title = "T" });
        t.Guests.AddRange(
            new Guest { Name = "A", Token = "TokenAAAA2", PhoneRaw = "0501111111", PhoneE164 = "+972501111111" },
            new Guest { Name = "B", Token = "TokenBBBB2", PhoneRaw = "0502222222", PhoneE164 = "+972502222222", InviteSentAtUtc = DateTime.UtcNow },
            new Guest { Name = "C", Token = "TokenCCCC2", PhoneRaw = "bad", PhoneError = "invalid" },
            new Guest { Name = "D", Token = "TokenDDDD2", PhoneRaw = "0504444444", PhoneE164 = "+972504444444", Status = RsvpStatus.Attending, AttendingCount = 1, RespondedAtUtc = DateTime.UtcNow.AddHours(-2) },
            new Guest { Name = "E", Token = "TokenEEEE2", PhoneRaw = "0505555555", PhoneE164 = "+972505555555", Status = RsvpStatus.Declined, RespondedAtUtc = DateTime.UtcNow.AddHours(-1) });
        t.SaveChanges();
    }

    private WeddingRsvp.Data.AppDbContext Db() { var t = new WeddingRsvp.Tenancy.TenantContext(); t.Use(_couple); return new WeddingRsvp.Data.AppDbContext(_options, t); }

    private GuestBulkService Service(WeddingRsvp.Data.AppDbContext db)
    {
        var tenant = new WeddingRsvp.Tenancy.TenantContext(); tenant.Use(_couple);
        var campaign = new CampaignService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new WeddingRsvp.Options.AppOptions()),
            new WeddingRsvp.Services.Sms.LogSmsProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<WeddingRsvp.Services.Sms.LogSmsProvider>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignService>.Instance);
        return new GuestBulkService(db, campaign, new FamilyService(db), new IsraelClock(TimeProvider.System));
    }

    private int[] Ids(WeddingRsvp.Data.AppDbContext db, params string[] names) => db.Guests.Where(g => names.Contains(g.Name)).Select(g => g.Id).ToArray();

    [Fact]
    public async Task Invites_go_only_to_valid_uninvited_guests()
    {
        using var db = Db();
        var r = await Service(db).SendInvitesAsync(Ids(db, "A", "B", "C"));
        Assert.Equal((1, 1, 1), (r.Queued, r.AlreadyInvited, r.NoPhone));
        Assert.Equal(1, await db.Messages.CountAsync(m => m.Kind == MessageKind.Invite && m.Status == MessageStatus.Queued));
    }

    [Fact]
    public async Task Set_invited_changes_the_limit_and_refuses_out_of_range()
    {
        using var db = Db();
        var svc = Service(db);
        Assert.Equal(2, await svc.SetInvitedAsync(Ids(db, "A", "B"), 3));
        Assert.Equal(-1, await svc.SetInvitedAsync(Ids(db, "A"), 51));
        Assert.Equal(2, await svc.SetInvitedAsync(Ids(db, "A", "B"), 0));
        Assert.All(db.Guests.Where(g => g.Name == "A" || g.Name == "B"), g => Assert.True(g.NoGuestLimit));
    }

    [Fact]
    public async Task Grouping_into_a_new_family_keeps_the_most_recent_answer_and_ungrouping_removes_empty_families()
    {
        using (var db = Db())
        {
            Assert.Equal(-1, await Service(db).SetFamilyAsync(Ids(db, "D", "E"), FamilyService.NewFamily, " "));
            Assert.Equal(3, await Service(db).SetFamilyAsync(Ids(db, "A", "D", "E"), FamilyService.NewFamily, "Levi"));
        }
        using (var db = Db())
        {
            var fam = await db.Families.SingleAsync();
            Assert.Equal("Levi", fam.Name);
            foreach (var g in db.Guests.Where(g => g.FamilyId == fam.Id)) Assert.Equal(RsvpStatus.Declined, g.Status); // E answered last
            Assert.Equal(3, await Service(db).SetFamilyAsync(Ids(db, "A", "D", "E"), FamilyService.None, null));
        }
        using var check = Db();
        Assert.Empty(check.Families);
    }

    [Fact]
    public async Task Delete_removes_guests_cancels_their_queued_messages_and_empty_families()
    {
        using (var db = Db())
        {
            await Service(db).SendInvitesAsync(Ids(db, "A"));
            await Service(db).SetFamilyAsync(Ids(db, "A", "B"), FamilyService.NewFamily, "X");
            Assert.Equal(2, await Service(db).DeleteAsync(Ids(db, "A", "B")));
        }
        using var check = Db();
        Assert.Equal(3, await check.Guests.CountAsync());
        Assert.Empty(check.Families);
        Assert.Equal(MessageStatus.Cancelled, (await check.Messages.SingleAsync()).Status);
    }

    public void Dispose() => _conn.Dispose();
}
