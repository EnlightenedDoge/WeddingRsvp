using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

public sealed class GuestTagTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple, _other;

    public GuestTagTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var a = new Couple { DisplayName = "A" };
        var b = new Couple { DisplayName = "B" };
        db.Couples.AddRange(a, b);
        db.SaveChanges();
        _couple = a.Id; _other = b.Id;
    }

    private AppDbContext Db(int? couple = null) { var t = new TenantContext(); t.Use(couple ?? _couple); return new AppDbContext(_options, t); }

    private async Task<ImportReport> ImportAsync(string csv, bool tagsOnly = false)
    {
        using var db = Db();
        return await new ImportService(db, new IsraelClock(TimeProvider.System), new FamilyService(db)).ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), tagsOnly);
    }

    private async Task<Dictionary<string, string?>> TagsAsync() { using var db = Db(); return await db.Guests.ToDictionaryAsync(g => g.Name, g => g.Tag); }

    // ---- normaliser
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Bride's   mother's  family ", "Bride's mother's family")]
    public void CleanTag_trims_and_collapses(string? input, string? expected) => Assert.Equal(expected, Guest.CleanTag(input));

    [Fact] public void CleanTag_cuts_to_the_limit() => Assert.Equal(Guest.TagMax, Guest.CleanTag(new string('x', 500))!.Length);

    // ---- import
    [Fact]
    public async Task Import_reads_an_english_tag_column()
    {
        await ImportAsync("name,phone,tag\nAmy,0501111111,Bride's mother's family\nBob,0502222222,\n");
        var t = await TagsAsync();
        Assert.Equal("Bride's mother's family", t["Amy"]);
        Assert.Null(t["Bob"]);
    }

    [Fact]
    public async Task Import_reads_a_hebrew_tag_column()
    {
        await ImportAsync("שם,טלפון,תגית\nדנה,0501111111,משפחת האם של הכלה\n");
        Assert.Equal("משפחת האם של הכלה", (await TagsAsync())["דנה"]);
    }

    [Fact]
    public async Task Import_without_a_tag_column_leaves_tags_empty()
    {
        await ImportAsync("name,phone\nAmy,0501111111\n");
        Assert.Null((await TagsAsync())["Amy"]);
    }

    [Fact]
    public async Task Import_does_not_mistake_the_family_columns_for_tags()
    {
        await ImportAsync("name,phone,family name,family id,tag\nAmy,0501111111,Cohen,F1,Friends\n");
        var t = await TagsAsync();
        Assert.Equal("Friends", t["Amy"]);
        using var db = Db();
        Assert.Equal("Cohen", (await db.Families.SingleAsync()).Name);
    }

    [Fact]
    public async Task Import_leaves_the_tag_of_a_duplicate_phone_alone()
    {
        await ImportAsync("name,phone,tag\nAmy,0501111111,Old\n");
        var r = await ImportAsync("name,phone,tag\nAmy again,0501111111,New\n");
        Assert.Single(r.Skipped);
        Assert.Equal("Old", (await TagsAsync())["Amy"]);
    }

    // ---- export
    [Fact]
    public async Task Export_includes_the_tag_and_round_trips_through_import()
    {
        await ImportAsync("name,phone,tag\nAmy,0501111111,\"Groom's father's family\"\n");
        List<Guest> guests;
        using (var db = Db()) guests = await db.Guests.Include(g => g.Family).AsNoTracking().ToListAsync();
        var csv = Encoding.UTF8.GetString(GuestCsv.Build(guests));
        Assert.Contains("תגית", csv.Split('\n')[0]);
        Assert.Contains("Groom's father's family", csv);

        using (var db = Db()) { db.Guests.RemoveRange(db.Guests); await db.SaveChangesAsync(); }
        await ImportAsync(csv.TrimStart('﻿'));
        Assert.Equal("Groom's father's family", (await TagsAsync())["Amy"]);
    }

    // ---- bulk
    private GuestBulkService Bulk(AppDbContext db) =>
        new(db, null!, new FamilyService(db), new IsraelClock(TimeProvider.System));

    private async Task SeedAsync()
    {
        using var db = Db();
        db.Guests.AddRange(
            new Guest { Name = "A", Token = "TokenAAAA2", PhoneRaw = "1", Tag = "Old" },
            new Guest { Name = "B", Token = "TokenBBBB2", PhoneRaw = "2" },
            new Guest { Name = "C", Token = "TokenCCCC2", PhoneRaw = "3", Tag = "Keep" });
        await db.SaveChangesAsync();
    }

    private async Task<int[]> IdsAsync(params string[] names) { using var db = Db(); return await db.Guests.Where(g => names.Contains(g.Name)).Select(g => g.Id).ToArrayAsync(); }

    [Fact]
    public async Task SetTag_sets_it_on_the_selected_guests_only_and_normalises()
    {
        await SeedAsync();
        using (var db = Db()) Assert.Equal(2, await Bulk(db).SetTagAsync(await IdsAsync("A", "B"), "  Work   friends "));
        var t = await TagsAsync();
        Assert.Equal("Work friends", t["A"]);
        Assert.Equal("Work friends", t["B"]);
        Assert.Equal("Keep", t["C"]);
    }

    [Fact]
    public async Task SetTag_with_an_empty_tag_clears_it()
    {
        await SeedAsync();
        using (var db = Db()) await Bulk(db).SetTagAsync(await IdsAsync("A", "C"), "  ");
        var t = await TagsAsync();
        Assert.Null(t["A"]);
        Assert.Null(t["C"]);
    }

    [Fact]
    public async Task SetTag_cannot_reach_another_couples_guest()
    {
        int foreign;
        using (var db = Db(_other))
        {
            var g = new Guest { Name = "Theirs", Token = "TokenZZZZ2", PhoneRaw = "9", Tag = "Theirs" };
            db.Guests.Add(g); await db.SaveChangesAsync(); foreign = g.Id;
        }
        await SeedAsync();
        using (var db = Db()) Assert.Equal(0, await Bulk(db).SetTagAsync([foreign], "Hacked"));
        using var check = Db(_other);
        Assert.Equal("Theirs", (await check.Guests.SingleAsync()).Tag);
    }

    // ---- list
    private async Task<Pages.Admin.Guests.IndexModel> ListAsync(string sort = "name", string dir = "asc", string? q = null)
    {
        var db = Db();
        var page = new Pages.Admin.Guests.IndexModel(db, new Ui(), new FamilyService(db), new IsraelClock(TimeProvider.System), null!) { Sort = sort, Dir = dir, Q = q };
        await page.OnGetAsync();
        return page;
    }

    private async Task SeedForListAsync()
    {
        using var db = Db();
        db.Guests.AddRange(
            new Guest { Name = "Zed", Token = "TokenAAAA2", PhoneRaw = "1", Tag = "Bride" },
            new Guest { Name = "Amy", Token = "TokenBBBB2", PhoneRaw = "2" },
            new Guest { Name = "Bob", Token = "TokenCCCC2", PhoneRaw = "3", Tag = "Groom" },
            new Guest { Name = "Cat", Token = "TokenDDDD2", PhoneRaw = "4", Tag = "Bride" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sort_by_tag_groups_them_and_puts_untagged_last()
    {
        await SeedForListAsync();
        Assert.Equal(["Cat", "Zed", "Bob", "Amy"], (await ListAsync("tag", "asc")).Guests.Select(g => g.Name));
    }

    [Fact]
    public async Task Sort_by_tag_descending()
    {
        await SeedForListAsync();
        // like the family sort, the untagged come first when descending
        Assert.Equal(["Amy", "Bob", "Cat", "Zed"], (await ListAsync("tag", "desc")).Guests.Select(g => g.Name));
    }

    [Fact]
    public async Task Search_matches_the_tag()
    {
        await SeedForListAsync();
        Assert.Equal(["Bob"], (await ListAsync(q: "groom")).Guests.Select(g => g.Name));
    }

    [Fact]
    public async Task Tag_options_are_distinct_sorted_and_only_this_couples()
    {
        await SeedForListAsync();
        using (var db = Db(_other)) { db.Guests.Add(new Guest { Name = "X", Token = "TokenYYYY2", PhoneRaw = "9", Tag = "Other couple" }); await db.SaveChangesAsync(); }
        Assert.Equal(["Bride", "Groom"], (await ListAsync()).TagOptions);
    }

    public void Dispose() => _conn.Dispose();

    // ---- tags-only import
    [Fact]
    public async Task TagsOnly_sets_the_tag_by_phone_and_changes_nothing_else()
    {
        await ImportAsync("name,phone,invited,tag\nAmy,0501111111,3,Old\nBob,0502222222,2,\n");
        var r = await ImportAsync("name,phone,tag\nWhatever,050-111-1111,New\nBob,+972502222222,Friends\n", tagsOnly: true);
        Assert.Equal(2, r.Updated);
        Assert.Equal(0, r.Added);
        using var db = Db();
        var amy = await db.Guests.SingleAsync(g => g.PhoneE164 == "+972501111111");
        Assert.Equal("New", amy.Tag);
        Assert.Equal("Amy", amy.Name);
        Assert.Equal(3, amy.InvitedCount);
        Assert.Equal("Friends", (await db.Guests.SingleAsync(g => g.Name == "Bob")).Tag);
    }

    [Fact]
    public async Task TagsOnly_never_adds_guests_and_reports_unknown_and_blank_rows()
    {
        await ImportAsync("name,phone,tag\nAmy,0501111111,Keep\n");
        var r = await ImportAsync("phone,tag\n0509999999,X\n0501111111,\nbad,Y\n", tagsOnly: true);
        Assert.Equal(0, r.Updated);
        Assert.Equal(3, r.Skipped.Count);
        using var db = Db();
        Assert.Equal(1, await db.Guests.CountAsync());
        Assert.Equal("Keep", (await db.Guests.SingleAsync()).Tag);   // a blank cell does not clear
    }

    [Fact]
    public async Task TagsOnly_needs_phone_and_tag_columns()
    {
        await ImportAsync("name,phone\nAmy,0501111111\n");
        var r = await ImportAsync("name,phone\nAmy,0501111111\n", tagsOnly: true);
        Assert.Equal(0, r.Updated);
        Assert.Single(r.Skipped);
    }

    [Fact]
    public async Task TagsOnly_does_not_touch_another_couples_guest()
    {
        using (var other = Db(_other)) { other.Guests.Add(new Guest { Name = "Zed", PhoneE164 = "+972503333333", PhoneRaw = "0503333333", Token = "ZZZZZZZZZZ", CoupleId = _other }); other.SaveChanges(); }
        var r = await ImportAsync("phone,tag\n0503333333,Hijack\n", tagsOnly: true);
        Assert.Equal(0, r.Updated);
        using var db = Db(_other);
        Assert.Null((await db.Guests.SingleAsync()).Tag);
    }
}
