using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>A new image started on a volume that already holds data: upgrade in place, keep the data, keep a copy first.</summary>
public sealed class DbUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wr-upgrade-" + Guid.NewGuid().ToString("N"));

    public DbUpgradeTests() => Directory.CreateDirectory(_dir);

    private AppDbContext Open() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "rsvp.db")};Pooling=False").Options, new TenantContext());

    [Fact]
    public void Existing_volume_is_upgraded_in_place_with_a_backup()
    {
        // The volume as an older image left it: only the first migrations applied, with a couple in it.
        using (var old = Open())
        {
            old.GetService<IMigrator>().Migrate("20261028142330_AddAttendingChildren");
            old.Database.ExecuteSqlRaw("INSERT INTO Couples (DisplayName, Suspended, CreatedAtUtc) VALUES ('Keep me', 0, '2026-10-28 10:00:00')");
        }

        string? backup;
        using (var db = Open()) backup = DbUpgrade.Run(db, _dir, NullLogger.Instance);

        using var after = Open();
        Assert.Empty(after.Database.GetPendingMigrations());
        Assert.Equal("Keep me", after.Couples.Single().DisplayName);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        // The backup is the database as it was before: old schema, same data.
        var copy = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={backup};Pooling=False").Options;
        using var b = new AppDbContext(copy, new TenantContext());
        Assert.Equal(["20261028100948_InitialCreate", "20261028120158_AddMessageOpenedAt", "20261028142330_AddAttendingChildren"],
            b.Database.GetAppliedMigrations());
        Assert.Equal("Keep me", b.Couples.Single().DisplayName);
    }

    [Fact]
    public void A_fresh_volume_is_created_without_a_backup_and_an_up_to_date_one_is_left_alone()
    {
        using (var db = Open()) Assert.Null(DbUpgrade.Run(db, _dir, NullLogger.Instance));
        using (var db = Open()) Assert.Null(DbUpgrade.Run(db, _dir, NullLogger.Instance));
        Assert.False(Directory.Exists(Path.Combine(_dir, "backups")));
    }

    [Fact]
    public void Backups_keep_the_3_newest_and_nothing_older_than_3_months()
    {
        var dir = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(dir);
        var now = DateTime.UtcNow;
        string Make(string name, int daysOld)
        {
            var f = Path.Combine(dir, name);
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, now.AddDays(-daysOld));
            return f;
        }
        var d1 = Make("rsvp-before-a.db", 1);
        var d10 = Make("rsvp-before-b.db", 10);
        var d60 = Make("rsvp-before-c.db", 60);
        var d70 = Make("rsvp-before-d.db", 70);     // a 4th: over the count
        var d100 = Make("rsvp-before-e.db", 100);   // too old
        var other = Make("notes.txt", 400);         // not ours: never touched

        Assert.Equal(2, DbUpgrade.PurgeOldBackups(_dir, now));
        Assert.True(File.Exists(d1) && File.Exists(d10) && File.Exists(d60) && File.Exists(other));
        Assert.False(File.Exists(d70));
        Assert.False(File.Exists(d100));

        // Only 2 files, but one is past 3 months: age still removes it.
        File.SetLastWriteTimeUtc(d60, now.AddDays(-91));
        Assert.Equal(1, DbUpgrade.PurgeOldBackups(_dir, now));
        Assert.False(File.Exists(d60));
        Assert.Equal(0, DbUpgrade.PurgeOldBackups(Path.Combine(_dir, "nowhere"), now)); // no folder yet: fine
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}
