using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

public sealed class GroupLinkTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _a, _b;
    private readonly string _code;

    public GroupLinkTests()
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
        foreach (var id in new[] { _a, _b })
        {
            var t = new TenantContext(); t.Use(id);
            using var d = new AppDbContext(_options, t);
            var s = new SettingsService(d, t).GetAsync().Result;
            s.CoupleNames = "X & Y"; s.StartsAtLocal = DateTime.Today.AddMonths(2); s.VenueName = "Hall";
            d.SaveChanges();
        }
        _code = Svc(_a, out var sd).CreateAsync("Work friends", 4, null).Result.Code;
        sd.Dispose();
    }

    public void Dispose() => _conn.Dispose();

    private GroupLinkService Svc(int? couple, out AppDbContext db)
    {
        var t = new TenantContext();
        if (couple is { } id) t.Use(id);
        db = new AppDbContext(_options, t);
        var settings = new SettingsService(db, t);
        var clock = new IsraelClock(TimeProvider.System);
        return new GroupLinkService(db, settings, new RsvpService(db, settings, clock, t), clock, t);
    }

    private static GroupPerson Adult(string f, string l, string? phone) => new(f, l, phone, false);
    private static GroupPerson Child(string f, string l, string? phone = null) => new(f, l, phone, true);

    [Fact]
    public async Task A_first_answer_creates_the_person_and_everyone_they_add_as_guests()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        var r = await svc.SubmitAsync(_code, "Noa", "Levi", "050-123-4567", true,
            [Adult("Amit", "Levi", "052-555-1234"), Child("Tal", "Levi")], false);
        Assert.Equal(GroupSubmitKind.Saved, r.Kind);
        Assert.Equal(["Noa Levi", "Amit Levi", "Tal Levi"], r.Names);

        var t = new TenantContext(); t.Use(_a);
        using var check = new AppDbContext(_options, t);
        var guests = await check.Guests.OrderBy(g => g.Id).ToListAsync();
        Assert.Equal(3, guests.Count);
        Assert.All(guests, g => { Assert.NotNull(g.GroupLinkId); Assert.Equal(RsvpStatus.Attending, g.Status); Assert.Equal(1, g.AttendingCount); });
        var child = guests.Single(g => g.IsChild);
        Assert.Null(child.PhoneE164);
        Assert.Null(child.PhoneError); // a child without a phone is not an invalid number
        Assert.Equal(1, child.AttendingChildren);
        Assert.Equal(guests[0].Id, child.AddedByGuestId);
    }

    [Fact]
    public async Task An_adult_needs_a_phone_but_a_child_does_not_and_a_given_child_phone_must_be_valid()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        var r = await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [Adult("Amit", "Levi", ""), Child("Tal", "Levi", "12")], false);
        Assert.Equal(GroupSubmitKind.Invalid, r.Kind);
        Assert.Contains(new GroupFieldError("adult[0].phone", "phone"), r.Errors);
        Assert.Contains(new GroupFieldError("child[0].phone", "phone"), r.Errors);
    }

    [Fact]
    public async Task Names_are_required_for_everyone_and_the_limit_counts_the_person_answering()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        var r = await svc.SubmitAsync(_code, "Noa", "", "0501234567", true, [Child("", "Levi")], false);
        Assert.Contains(new GroupFieldError("me.last", "name"), r.Errors);
        Assert.Contains(new GroupFieldError("child[0].first", "name"), r.Errors);

        var five = Enumerable.Range(0, 4).Select(i => Child("K" + i, "Levi")).ToList(); // 1 + 4 = 5 > 4
        r = await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, five, false);
        Assert.Contains(new GroupFieldError("count", "max"), r.Errors);
    }

    [Fact]
    public async Task A_phone_used_twice_or_already_taken_is_refused()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        Assert.Equal(GroupSubmitKind.Saved, (await svc.SubmitAsync(_code, "Yael", "Cohen", "054-777-8899", true, [], false)).Kind);

        var r = await svc.SubmitAsync(_code, "Noa", "Levi", "050-123-4567", true,
            [Adult("Amit", "Levi", "0541234567"), Adult("Dan", "Levi", "054-123-4567"), Adult("Yael", "C", "054-777-8899")], false);
        Assert.Equal(GroupSubmitKind.Invalid, r.Kind);
        Assert.Contains(new GroupFieldError("adult[1].phone", "duplicate"), r.Errors); // twice in the same answer
        Assert.Contains(new GroupFieldError("adult[2].phone", "duplicate"), r.Errors); // already counted elsewhere
    }

    [Fact]
    public async Task Reopening_with_the_same_phone_replaces_the_earlier_answer_and_declining_removes_the_added_people()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [Adult("Amit", "Levi", "0521111111")], false);

        var look = await svc.LookupAsync("050-123-4567");
        Assert.Equal(GroupLookupKind.Returning, look.Kind);

        await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [Adult("Amit", "Levi", "0521111111"), Child("Tal", "Levi")], false);
        var t = new TenantContext(); t.Use(_a);
        using (var check = new AppDbContext(_options, t)) Assert.Equal(3, await check.Guests.CountAsync());

        await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", false, [Adult("Amit", "Levi", "0521111111")], false);
        using var check2 = new AppDbContext(_options, t);
        var only = await check2.Guests.SingleAsync();
        Assert.Equal(RsvpStatus.Declined, only.Status);
        Assert.Equal(0, only.AttendingCount);
    }

    [Fact]
    public async Task Someone_added_by_another_is_counted_and_can_take_over_to_answer_for_themselves()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [Adult("Yael", "Cohen", "054-777-8899")], false);

        var look = await svc.LookupAsync("0547778899");
        Assert.Equal(GroupLookupKind.CountedByOther, look.Kind);
        Assert.Equal("Noa Levi", look.OwnerName);
        Assert.Equal(GroupSubmitKind.CountedByOther, (await svc.SubmitAsync(_code, "Yael", "Cohen", "0547778899", true, [], false)).Kind);

        Assert.Equal(GroupSubmitKind.Saved, (await svc.SubmitAsync(_code, "Yael", "Cohen", "0547778899", true, [Child("Ron", "Cohen")], true)).Kind);
        var t = new TenantContext(); t.Use(_a);
        using var check = new AppDbContext(_options, t);
        var yael = await check.Guests.SingleAsync(g => g.Name == "Yael Cohen");
        Assert.Null(yael.AddedByGuestId);
        var responses = await new GroupLinkService(check, new SettingsService(check, t), null!, new IsraelClock(TimeProvider.System), t)
            .ResponsesAsync(yael.GroupLinkId!.Value);
        Assert.Equal(2, responses.Count);
        Assert.Equal(1, responses.Single(r => r.Who.Name == "Noa Levi").People); // Yael left Noa's list
        Assert.Equal(2, responses.Single(r => r.Who.Name == "Yael Cohen").People);
    }

    [Fact]
    public async Task A_number_from_the_couples_own_list_is_pointed_to_its_personal_link()
    {
        var t = new TenantContext(); t.Use(_a);
        using (var db = new AppDbContext(_options, t))
        {
            db.Guests.Add(new Guest { CoupleId = _a, Name = "Cohen family", PhoneE164 = "+972501234567", Token = "ABCDEFGHJK", InvitedCount = 3 });
            await db.SaveChangesAsync();
        }
        var svc = Svc(null, out var db2);
        using var _ = db2;
        await svc.FindAsync(_code); // the public page does this first: it picks the couple
        Assert.Equal(GroupLookupKind.OnList, (await svc.LookupAsync("050-123-4567")).Kind);
        Assert.Equal(GroupSubmitKind.OnList, (await svc.SubmitAsync(_code, "Noa", "Levi", "050-123-4567", true, [], false)).Kind);
    }

    [Fact]
    public async Task A_link_that_is_off_or_expired_accepts_no_answers_and_extending_reopens_it()
    {
        var svc = Svc(_a, out var db);
        using var _ = db;
        var l = await svc.GetAsync(1);
        Assert.NotNull(l);
        var ta = new TenantContext(); ta.Use(_a);
        Assert.Equal(GroupLinkStatus.Active, svc.StatusOf(l!, await new SettingsService(db, ta).GetAsync()));

        await svc.SetOffAsync(l!.Id, true);
        var pub = Svc(null, out var db2);
        using var __ = db2;
        Assert.Equal(GroupSubmitKind.Closed, (await pub.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [], false)).Kind);

        await svc.SetOffAsync(l.Id, false);
        await svc.UpdateAsync(l.Id, l.Name, 4, DateTime.Now.AddDays(-1));
        Assert.Equal(GroupSubmitKind.Closed, (await pub.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [], false)).Kind);

        await svc.UpdateAsync(l.Id, l.Name, 4, DateTime.Now.AddDays(3));
        var fresh = Svc(null, out var db3); // a new request: the earlier context still holds the old expiry
        using var ___ = db3;
        Assert.Equal(GroupSubmitKind.Saved, (await fresh.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [], false)).Kind);
    }

    [Fact]
    public async Task The_code_picks_the_couple_and_one_couple_never_sees_the_others_links_or_guests()
    {
        var pub = Svc(null, out var db);
        using var _ = db;
        await pub.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [], false);

        var other = Svc(_b, out var dbB);
        using var __ = dbB;
        Assert.Empty(await other.ListAsync());
        Assert.Empty(await dbB.Guests.ToListAsync());
        Assert.Null(await other.GetAsync(1));
        Assert.Null(await pub.FindAsync("ZZZZZZ"));
        Assert.Null(await pub.FindAsync("bad"));
    }

    [Fact]
    public async Task Group_guests_are_never_queued_for_an_invitation_and_the_dashboard_does_not_count_them_as_unsent()
    {
        var svc = Svc(null, out var db);
        using var _ = db;
        await svc.SubmitAsync(_code, "Noa", "Levi", "0501234567", true, [Child("Tal", "Levi")], false);

        var t = new TenantContext(); t.Use(_a);
        using var check = new AppDbContext(_options, t);
        var stats = await new DashboardService(check).GetAsync();
        Assert.Equal(0, stats.InvitesNotSent);
        Assert.Equal(0, stats.InvalidPhones);
        Assert.Equal(2, stats.AttendingPeople);
        Assert.Equal(1, stats.AttendingChildren);
    }

    [Fact]
    public async Task A_link_with_no_guest_limit_takes_many_people_up_to_the_cap_like_an_unlimited_invitation()
    {
        var admin = Svc(_a, out var adb);
        using var _ = adb;
        var open = await admin.CreateAsync("Everyone", 0, null);
        Assert.True(open.NoGuestLimit);
        Assert.Equal(Guest.UnlimitedCap, open.EffectiveMax);
        Assert.Null(GroupLinkService.Validate("Everyone", 0));
        Assert.NotNull(GroupLinkService.Validate("Everyone", -1));

        var pub = Svc(null, out var db);
        using var __ = db;
        var twenty = Enumerable.Range(0, 20).Select(i => Adult("A" + i, "Z", $"052-600-{i:0000}")).ToList();
        Assert.Equal(GroupSubmitKind.Saved, (await pub.SubmitAsync(open.Code, "Noa", "Levi", "0501234567", true, twenty, false)).Kind);

        var tooMany = Enumerable.Range(0, Guest.UnlimitedCap).Select(i => Child("K" + i, "Z")).ToList(); // 1 + 99 > 99
        var r = await pub.SubmitAsync(open.Code, "Dan", "Levi", "0507654321", true, tooMany, false);
        Assert.Contains(new GroupFieldError("count", "max"), r.Errors);
    }
}
