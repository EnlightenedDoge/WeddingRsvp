using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public enum RsvpResult { Saved, NotFound, Closed, InvalidCount, MissingAnswer }

/// <summary>Everything a guest can do through their personal link.</summary>
public sealed class RsvpService(AppDbContext db, SettingsService settings, IsraelClock clock)
{
    public async Task<Guest?> FindAsync(string token, CancellationToken ct = default) =>
        TokenGenerator.LooksValid(token)
            ? await db.Guests.FirstOrDefaultAsync(g => g.Token == token, ct)
            : null;

    /// <summary>Loads the guest and records that the link was opened.</summary>
    public async Task<Guest?> OpenAsync(string token, CancellationToken ct = default)
    {
        var g = await FindAsync(token, ct);
        if (g is null) return null;
        g.FirstOpenedAtUtc ??= clock.UtcNow;
        g.LastOpenedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return g;
    }

    public async Task<bool> IsClosedAsync(CancellationToken ct = default) =>
        (await settings.GetAsync(ct)).StartsAtLocal is not { } startsAt || clock.LocalNow >= startsAt;

    public async Task<RsvpResult> SubmitAsync(string token, bool? attending, int count, CancellationToken ct = default)
    {
        var g = await FindAsync(token, ct);
        if (g is null) return RsvpResult.NotFound;
        if (await IsClosedAsync(ct)) return RsvpResult.Closed;
        if (attending is null) return RsvpResult.MissingAnswer;
        if (attending == true && (count < 1 || count > g.InvitedCount)) return RsvpResult.InvalidCount;

        g.Status = attending.Value ? RsvpStatus.Attending : RsvpStatus.Declined;
        g.AttendingCount = attending.Value ? count : 0;
        g.Source = RsvpSource.Web;
        g.RespondedAtUtc = clock.UtcNow;

        await CancelQueuedRemindersAsync(g.Id, ct);
        await db.SaveChangesAsync(ct);
        return RsvpResult.Saved;
    }

    private async Task CancelQueuedRemindersAsync(int guestId, CancellationToken ct)
    {
        var queued = await db.Messages
            .Where(m => m.GuestId == guestId && m.Kind == MessageKind.Reminder && m.Status == MessageStatus.Queued)
            .ToListAsync(ct);
        foreach (var m in queued)
        {
            m.Status = MessageStatus.Cancelled;
            m.Error = "Guest responded";
        }
    }
}
