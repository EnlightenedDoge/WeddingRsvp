namespace WeddingRsvp.Services;

/// <summary>Removes upgrade backups beyond the 3 newest or older than 3 months, at startup and then every day (the app may run for months between upgrades).</summary>
public sealed class BackupCleanupService(Microsoft.Extensions.Options.IOptions<WeddingRsvp.Options.AppOptions> options, ILogger<BackupCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dir = Path.GetFullPath(options.Value.DataDir);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                var n = DbUpgrade.PurgeOldBackups(dir, DateTime.UtcNow);
                if (n > 0) log.LogInformation("Deleted {Count} old database backups", n);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not clean up old database backups");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
