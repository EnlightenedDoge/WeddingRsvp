using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>The optional dietary question: counts per kind of meal (0..people attending) plus a note, only stored while the couple asks.</summary>
public sealed class DietaryTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public DietaryTests()
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
        t.Guests.Add(new Guest { Name = "four", Token = "Fam4Guests", PhoneRaw = "0501111111", PhoneE164 = "+972501111111", InvitedCount = 4 });
        t.SaveChanges();
    }

    private AppDbContext Db() { var t = new TenantContext(); t.Use(_couple); return new AppDbContext(_options, t); }

    private async Task<RsvpResult> SubmitAsync(bool? attending, int adults, int children, DietaryAnswer? diet)
    {
        var tenant = new TenantContext();
        using var db = new AppDbContext(_options, tenant);
        var svc = new RsvpService(db, new SettingsService(db, tenant), new IsraelClock(TimeProvider.System), tenant);
        return await svc.SubmitAsync("Fam4Guests", attending, adults, children, diet);
    }

    private async Task<Guest> GuestAsync() { using var db = Db(); return await db.Guests.SingleAsync(); }

    [Fact]
    public async Task Counts_and_note_are_saved()
    {
        Assert.Equal(RsvpResult.Saved, await SubmitAsync(true, 2, 1, new DietaryAnswer(1, 1, 2, "  nut allergy ")));
        var g = await GuestAsync();
        Assert.Equal((1, 1, 2, "nut allergy"), (g.DietVegetarian, g.DietVegan, g.DietGlutenFree, g.DietNote));
    }

    [Fact]
    public async Task More_than_the_people_attending_is_refused_and_nothing_changes()
    {
        Assert.Equal(RsvpResult.InvalidDiet, await SubmitAsync(true, 1, 1, new DietaryAnswer(3, 0, 0, null)));
        Assert.Equal(RsvpResult.InvalidDiet, await SubmitAsync(true, 2, 0, new DietaryAnswer(0, -1, 0, null)));
        Assert.Equal(RsvpResult.InvalidDiet, await SubmitAsync(true, 2, 0, new DietaryAnswer(0, 0, 0, new string('x', Guest.DietNoteMax + 1))));
        var g = await GuestAsync();
        Assert.Equal(RsvpStatus.Pending, g.Status);
        Assert.False(g.HasDiet);
    }

    [Fact]
    public async Task Declining_clears_an_earlier_dietary_answer()
    {
        await SubmitAsync(true, 2, 0, new DietaryAnswer(1, 0, 0, "x"));
        Assert.Equal(RsvpResult.Saved, await SubmitAsync(false, 1, 0, null));
        Assert.False((await GuestAsync()).HasDiet);
    }

    [Fact]
    public async Task Nothing_is_stored_while_the_couple_is_not_asking()
    {
        using (var db = Db()) { (await db.EventSettings.SingleAsync()).AskDietary = false; await db.SaveChangesAsync(); }
        Assert.Equal(RsvpResult.Saved, await SubmitAsync(true, 2, 0, new DietaryAnswer(1, 0, 0, "x")));
        Assert.False((await GuestAsync()).HasDiet);
    }

    public void Dispose() => _conn.Dispose();
}

public sealed class ChildAgeLabelTests
{
    [Theory]
    [InlineData(3, 12, true, "גילאי 3–12")]
    [InlineData(3, 12, false, "ages 3–12")]
    [InlineData(null, 12, false, "up to age 12")]
    [InlineData(null, 12, true, "עד גיל 12")]
    [InlineData(3, null, false, "from age 3")]
    [InlineData(5, 5, false, "age 5")]
    [InlineData(null, null, false, null)]
    public void Label_reads_naturally(int? min, int? max, bool he, string? expected) =>
        Assert.Equal(expected, new WeddingRsvp.Domain.EventSettings { ChildMinAge = min, ChildMaxAge = max }.ChildAgeLabel(he));
}
