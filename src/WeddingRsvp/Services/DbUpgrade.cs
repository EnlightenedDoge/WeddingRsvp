using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WeddingRsvp.Data;

namespace WeddingRsvp.Services;

/// <summary>
/// Brings the database to the current schema at startup. When a database that already has data is about to be upgraded
/// (a new image on an existing volume), a copy is made first, so a bad migration can be undone by restoring the file.
/// </summary>
public static class DbUpgrade
{
    /// <summary>Backups hold guest names and phone numbers: at most this many are kept, and none older than the retention.</summary>
    private const int KeepBackups = 3;
    public static readonly TimeSpan BackupRetention = TimeSpan.FromDays(90);

    /// <returns>The backup file written, or null when nothing needed upgrading or the database is new.</returns>
    public static string? Run(AppDbContext db, string dataDir, ILogger log)
    {
        if (!db.Database.GetMigrations().Any()) return null;

        string? backup = null;
        var pending = db.Database.GetPendingMigrations().ToList();
        var applied = db.Database.GetAppliedMigrations().ToList();
        if (pending.Count > 0 && applied.Count > 0)
        {
            backup = Backup(db, dataDir, applied[^1]);
            log.LogInformation("Upgrading the database from {From} to {To} ({Count} migrations); backup: {Backup}",
                applied[^1], pending[^1], pending.Count, backup);
        }
        db.Database.Migrate();
        PurgeOldBackups(dataDir, DateTime.UtcNow);
        return backup;
    }

    private static string Backup(AppDbContext db, string dataDir, string lastApplied)
    {
        var dir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"rsvp-before-{lastApplied}-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
        // VACUUM INTO writes a consistent copy even while the WAL file holds recent changes.
        db.Database.ExecuteSqlRaw("VACUUM INTO {0}", file);
        PurgeOldBackups(dataDir, DateTime.UtcNow);
        return file;
    }

    /// <summary>Deletes backups beyond the 3 newest, and any older than <see cref="BackupRetention"/> (3 months). Returns how many were removed.</summary>
    public static int PurgeOldBackups(string dataDir, DateTime utcNow)
    {
        var dir = Path.Combine(dataDir, "backups");
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        // Newest first (the file name starts with the migration, so order by write time): the 3 newest may stay, if young enough.
        var files = Directory.GetFiles(dir, "rsvp-before-*.db").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        foreach (var f in files.Where((f, i) => i >= KeepBackups || utcNow - File.GetLastWriteTimeUtc(f) > BackupRetention))
            {
                try { File.Delete(f); n++; } catch (IOException) { } // retried at the next sweep
            }
        return n;
    }
}
