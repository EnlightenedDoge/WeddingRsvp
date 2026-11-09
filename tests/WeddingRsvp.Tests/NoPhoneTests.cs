using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Pages.Admin.Guests;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>
/// A guest the couple has no usable number for (the cell was empty, or holds a landline). Such a guest is still kept: they have
/// an invitation, a personal link the couple can hand over by other means, and they count towards the totals. What must never
/// happen is a message with no recipient, so every planner skips them, and the list and dashboard show them as a number to fix.
/// A child added through a group link also has no number, but that is not a problem to fix — the last two tests cover the difference.
/// </summary>
public sealed class NoPhoneTests : IDisposable
{
    // Tuesday 2026-11-10 10:00 UTC = 12:00 in Israel.
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    }

    private static readonly DateTime FutureEvent = new(2026, 12, 10, 19, 0, 0);
    /// <summary>The evening before "now", so the thank-you planner fires at its hour on the day after.</summary>
    private static readonly DateTime PastEvent = new(2026, 11, 9, 19, 0, 0);

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;
    private static readonly IsraelClock Clock = new(new FixedTime());

    public NoPhoneTests()
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
        // Everything the templates need, so nothing below is blocked by a missing event detail.
        t.EventSettings.Add(new EventSettings
        {
            CoupleNames = "דנה ויוסי", Title = "חתונה", VenueName = "אולם", StartsAtLocal = FutureEvent,
        });
        t.SaveChanges();
    }

    private TenantContext Tenant() { var t = new TenantContext(); t.Use(_couple); return t; }
    private AppDbContext Db() => new(_options, Tenant());

    private CampaignService Campaign(AppDbContext db, ITenantContext tenant) =>
        new(db, new SettingsService(db, tenant), Clock, Microsoft.Extensions.Options.Options.Create(new AppOptions()),
            new LogSmsProvider(NullLogger<LogSmsProvider>.Instance), NullLogger<CampaignService>.Instance);

    private async Task<ImportReport> ImportAsync(string csv)
    {
        using var db = Db();
        return await new ImportService(db, Clock, new FamilyService(db))
            .ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
    }

    private async Task<List<Guest>> GuestsAsync()
    {
        using var db = Db();
        return await db.Guests.Include(g => g.Family).OrderBy(g => g.Name).ToListAsync();
    }

    /// <summary>One guest the couple can message and one they can't, both invited for two.</summary>
    private async Task SeedAsync()
    {
        using var db = Db();
        db.Guests.AddRange(
            new Guest { Name = "Dana", Token = "TokenAAAA2", PhoneRaw = "050-1234567", PhoneE164 = "+972501234567", InvitedCount = 2, CreatedAtUtc = Clock.UtcNow },
            new Guest { Name = "Noam", Token = "TokenBBBB2", PhoneRaw = "", PhoneError = "חסר מספר", InvitedCount = 2, CreatedAtUtc = Clock.UtcNow });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("", "חסר מספר")]
    [InlineData("   ", "חסר מספר")]
    [InlineData(null, "חסר מספר")]
    [InlineData("03-1234567", "לא מספר נייד")]   // a landline can't receive an SMS
    [InlineData("12345", "מספר לא תקין")]
    public void A_missing_number_is_reported_apart_from_a_wrong_one(string? raw, string problem)
    {
        var r = PhoneNormalizer.Normalize(raw);
        Assert.Null(r.E164);
        Assert.False(r.IsValid);
        Assert.Equal(problem, r.Error);   // the list and the edit page show this through Labels.Problem
    }

    [Fact]
    public async Task Import_keeps_a_guest_without_a_number_and_lists_them_as_one_to_fix()
    {
        var report = await ImportAsync("name,phone,invited\nDana,050-1234567,2\nNoam,,2\nGil,03-1234567,1\n,0509999999,1\n");

        Assert.Equal(4, report.Rows);
        Assert.Equal(3, report.Added);   // a row with no name is not a guest at all; a row with no number is
        Assert.Equal("חסר שם", Assert.Single(report.Skipped).Problem);
        Assert.Equal(new[] { "חסר מספר", "לא מספר נייד" }, report.InvalidPhones.Select(i => i.Problem).ToArray());

        var noam = (await GuestsAsync()).Single(g => g.Name == "Noam");
        Assert.Null(noam.PhoneE164);
        Assert.Equal("חסר מספר", noam.PhoneError);
        Assert.False(noam.HasValidPhone);
        Assert.Equal(2, noam.InvitedCount);                 // still an invitation for two
        Assert.True(TokenGenerator.LooksValid(noam.Token));  // and still has a personal link to hand over
    }

    [Fact]
    public async Task Several_guests_without_a_number_are_all_imported_because_only_real_numbers_can_clash()
    {
        var report = await ImportAsync("name,phone\nNoam,\nGil,\nDana,050-1234567\nDana again,050-1234567\n");

        Assert.Equal(3, report.Added);
        Assert.Equal("מספר כפול", Assert.Single(report.Skipped).Problem);   // only the repeated real number
        Assert.Equal(2, (await GuestsAsync()).Count(g => !g.HasValidPhone));
    }

    [Fact]
    public async Task Invitations_skip_a_guest_without_a_number_and_reach_them_once_it_is_fixed()
    {
        await SeedAsync();
        using var db = Db();
        var campaign = Campaign(db, Tenant());

        Assert.Equal(1, (await campaign.InviteCostAsync()).Guests);   // the Messages page counts only who can be reached
        Assert.Equal(1, await campaign.QueueInvitesAsync());

        var noam = await db.Guests.SingleAsync(g => g.Name == "Noam");
        Assert.False(await campaign.QueueInviteAsync(noam.Id));       // nor one by one from the guest's own page
        Assert.Equal("+972501234567", (await db.Messages.SingleAsync()).ToE164);

        noam.PhoneRaw = "052-765-4321";
        noam.PhoneE164 = "+972527654321";
        noam.PhoneError = null;
        await db.SaveChangesAsync();

        Assert.True(await campaign.QueueInviteAsync(noam.Id));
        Assert.Equal(2, await db.Messages.CountAsync());
        Assert.All(await db.Messages.ToListAsync(), m => Assert.False(string.IsNullOrWhiteSpace(m.ToE164)));
    }

    [Fact]
    public async Task Reminders_skip_a_guest_whose_number_was_removed_after_the_invitation_went_out()
    {
        await SeedAsync();
        using var db = Db();
        foreach (var g in await db.Guests.ToListAsync()) g.InviteSentAtUtc = Clock.UtcNow.AddDays(-10);
        await db.SaveChangesAsync();

        await Campaign(db, Tenant()).PlanAsync();   // event is still ahead, so this is the reminder round

        var m = Assert.Single(await db.Messages.ToListAsync());
        Assert.Equal(MessageKind.Reminder, m.Kind);
        Assert.Equal("+972501234567", m.ToE164);
    }

    [Fact]
    public async Task Thank_yous_skip_a_guest_without_a_number_even_though_they_answered()
    {
        await SeedAsync();
        using var db = Db();
        var tenant = Tenant();
        foreach (var g in await db.Guests.ToListAsync())
        {
            g.Status = RsvpStatus.Attending;
            g.AttendingCount = 2;
            g.RespondedAtUtc = Clock.UtcNow.AddDays(-3);
        }
        var s2 = await new SettingsService(db, tenant).GetAsync();
        s2.StartsAtLocal = PastEvent;
        s2.ThankYouHourLocal = 10;   // 10:00 on the day after, two hours before "now"
        await db.SaveChangesAsync();

        await Campaign(db, tenant).PlanAsync();   // the day after the wedding

        var m = Assert.Single(await db.Messages.ToListAsync());
        Assert.Equal(MessageKind.ThankYou, m.Kind);
        Assert.Equal("+972501234567", m.ToE164);
    }

    [Fact]
    public async Task The_bulk_send_reports_the_guests_it_cannot_reach()
    {
        await SeedAsync();
        using var db = Db();
        var ids = await db.Guests.Select(g => g.Id).ToListAsync();

        var r = await new GuestBulkService(db, Campaign(db, Tenant()), new FamilyService(db), Clock).SendInvitesAsync(ids);

        Assert.Equal((1, 1, 0), (r.Queued, r.NoPhone, r.AlreadyInvited));
        Assert.Empty(r.MissingFields);
        Assert.Equal("+972501234567", (await db.Messages.SingleAsync()).ToE164);
        Assert.Equal(2, await db.Guests.CountAsync());   // the one with no number is left alone, not dropped
    }

    [Fact]
    public async Task The_dashboard_and_the_list_flag_a_missing_number_but_still_count_the_invitation()
    {
        await SeedAsync();
        using var db = Db();

        var stats = await new DashboardService(db).GetAsync();
        Assert.Equal(2, stats.Invitations);
        Assert.Equal(4, stats.InvitedPeople);   // 2 + 2: an invitation counts even with no number to send it to
        Assert.Equal(1, stats.InvalidPhones);
        Assert.Equal(2, stats.InvitesNotSent);  // still work to do: the number has to be found first

        var page = new IndexModel(db, new Ui(), new FamilyService(db), Clock, null!) { Filter = "invalid" };
        await page.OnGetAsync();
        Assert.Equal("Noam", Assert.Single(page.Guests).Name);
    }

    [Fact]
    public async Task A_guest_without_a_number_can_still_answer_through_the_link_the_couple_hands_them()
    {
        await SeedAsync();
        string token;
        using (var db = Db()) token = (await db.Guests.SingleAsync(g => g.Name == "Noam")).Token;

        var tenant = new TenantContext();   // the public flow has no tenant until the token resolves it
        using (var db = new AppDbContext(_options, tenant))
        {
            var svc = new RsvpService(db, new SettingsService(db, tenant), Clock, tenant);
            Assert.Equal(RsvpResult.Saved, await svc.SubmitAsync(token, true, 2, 0));
        }

        using var check = Db();
        var noam = await check.Guests.SingleAsync(g => g.Name == "Noam");
        Assert.Equal(RsvpStatus.Attending, noam.Status);
        Assert.Equal(RsvpSource.Web, noam.Source);
        var stats = await new DashboardService(check).GetAsync();
        Assert.Equal(2, stats.AttendingPeople);
        Assert.Equal(1, stats.InvalidPhones);   // answered, but there is still no number on file
    }

    [Fact]
    public async Task The_edit_page_saves_a_guest_with_no_number_and_two_of_them_do_not_collide()
    {
        using (var db = Db())
        {
            var page = EditPage(db);
            page.Input = new EditModel.GuestInput { Name = "Noam", Phone = "", InvitedCount = 2 };
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(null));
        }
        using (var db = Db())
        {
            // A second one: the "this number already belongs to another guest" rule must not fire on two empty numbers.
            var page = EditPage(db);
            page.Input = new EditModel.GuestInput { Name = "Gil", Phone = "   ", InvitedCount = 1 };
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(null));
            Assert.True(page.ModelState.IsValid);
        }

        var guests = await GuestsAsync();
        Assert.Equal(2, guests.Count);
        Assert.All(guests, g =>
        {
            Assert.Null(g.PhoneE164);
            Assert.Equal("חסר מספר", g.PhoneError);
            Assert.True(TokenGenerator.LooksValid(g.Token));
        });
        Assert.Equal(2, guests.Select(g => g.Token).Distinct().Count());
    }

    [Fact]
    public async Task Clearing_a_number_on_the_edit_page_keeps_the_guest_and_their_answer()
    {
        await SeedAsync();
        int id;
        using (var db = Db())
        {
            var dana = await db.Guests.SingleAsync(g => g.Name == "Dana");
            id = dana.Id;
            dana.Status = RsvpStatus.Attending;
            dana.AttendingCount = 2;
            dana.InviteSentAtUtc = Clock.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        using (var db = Db())
        {
            var page = EditPage(db);
            await page.OnGetAsync(id);
            page.Input.Phone = "";   // the couple found out the number was someone else's
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(id));
        }

        using (var check = Db())
        {
            var dana = await check.Guests.SingleAsync(g => g.Id == id);
            Assert.Null(dana.PhoneE164);
            Assert.Equal("חסר מספר", dana.PhoneError);
            Assert.Equal("", dana.PhoneRaw);
            Assert.Equal(RsvpStatus.Attending, dana.Status);   // the answer stands
            Assert.Equal(2, dana.AttendingCount);
            Assert.NotNull(dana.InviteSentAtUtc);              // and so does the record of what was sent
        }
        using (var db = Db())
        {
            // Sending again from the guest's own page is refused, with the event details all in place.
            var page = EditPage(db);
            Assert.IsType<RedirectToPageResult>(await page.OnPostSendInviteAsync(id));
            Assert.Equal(0, await db.Messages.CountAsync());
        }
    }

    [Fact]
    public async Task A_group_links_child_has_no_number_but_is_not_a_number_to_fix()
    {
        using (var db = Db())
        {
            var svc = GroupSvc(db);
            var link = await svc.CreateAsync("Work friends", 4, null);
            var r = await svc.SubmitAsync(link.Code, "Noa", "Levi", "052-765-4321", true,
                [new GroupPerson("Tal", "Levi", null, true)], false);
            Assert.Equal(GroupSubmitKind.Saved, r.Kind);
        }

        using (var db = Db())
        {
            var child = await db.Guests.SingleAsync(g => g.IsChild);
            Assert.Null(child.PhoneE164);
            Assert.Null(child.PhoneError);   // no number was ever asked for, so there is nothing to fix

            var stats = await new DashboardService(db).GetAsync();
            Assert.Equal(0, stats.InvalidPhones);
            Assert.Equal(0, stats.InvitesNotSent);   // group guests are never invited by SMS

            var page = new IndexModel(db, new Ui(), new FamilyService(db), Clock, null!) { Filter = "invalid" };
            await page.OnGetAsync();
            Assert.Empty(page.Guests);
        }
    }

    [Fact]
    public async Task The_thank_you_reaches_the_adult_of_a_group_answer_but_not_the_child_with_no_number()
    {
        using (var db = Db())
        {
            var svc = GroupSvc(db);
            var link = await svc.CreateAsync("Work friends", 4, null);
            await svc.SubmitAsync(link.Code, "Noa", "Levi", "052-765-4321", true,
                [new GroupPerson("Tal", "Levi", null, true)], false);
        }
        using (var db = Db())
        {
            var tenant = Tenant();
            var s2 = await new SettingsService(db, tenant).GetAsync();
            s2.StartsAtLocal = PastEvent;
            s2.ThankYouHourLocal = 10;
            await db.SaveChangesAsync();

            await Campaign(db, tenant).PlanAsync();

            var m = Assert.Single(await db.Messages.ToListAsync());
            Assert.Equal(MessageKind.ThankYou, m.Kind);
            Assert.Equal("+972527654321", m.ToE164);
        }
    }

    private GroupLinkService GroupSvc(AppDbContext db)
    {
        var tenant = Tenant();
        var settings = new SettingsService(db, tenant);
        return new GroupLinkService(db, settings, new RsvpService(db, settings, Clock, tenant), Clock, tenant);
    }

    private EditModel EditPage(AppDbContext db)
    {
        var tenant = Tenant();
        return new EditModel(db, Campaign(db, tenant), Clock,
            Microsoft.Extensions.Options.Options.Create(new AppOptions()), new Ui(), new FamilyService(db))
        {
            PageContext = new PageContext(new ActionContext(
                new DefaultHttpContext(), new RouteData(), new PageActionDescriptor(), new ModelStateDictionary())),
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NoTempData()),
        };
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    public void Dispose() => _conn.Dispose();
}
