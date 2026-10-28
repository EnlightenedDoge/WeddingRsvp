using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Services;

public enum RsvpResult { Saved, NotFound, Closed, InvalidCount, MissingAnswer }

/// <summary>Everything a guest can do through their personal link.</summary>
public sealed class RsvpService(AppDbContext db, SettingsService settings, IsraelClock clock, ITenantContext tenant)
{
    /// <summary>
    /// The one place the public flow leaves tenant scope: the token is globally unique and unguessable, and it
    /// decides which couple this request acts for. After this call all queries are scoped to that couple.
    /// </summary>
    public async Task<Guest?> FindAsync(string token, CancellationToken ct = default)
    {
        if (!TokenGenerator.LooksValid(token)) return null;
        var g = await db.Guests.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Token == token, ct);
        if (g is not null) tenant.Use(g.CoupleId);
        return g;
    }

    /// <summary>
    /// A person really looked at the page: the browser ran the page and reported back (see the "seen" beacon). Loading the page
    /// alone does not count, because link scanners, previews and checkers fetch it within a second of the SMS being sent.
    /// This also proves the SMS arrived, so the guest's sent or delivered messages become Opened (and Delivered if no provider report
    /// came first).
    /// </summary>
    public async Task<bool> SeenAsync(string token, CancellationToken ct = default)
    {
        var g = await FindAsync(token, ct);
        if (g is null) return false;
        g.FirstOpenedAtUtc ??= clock.UtcNow;
        g.LastOpenedAtUtc = clock.UtcNow;
        var arrived = await db.Messages
            .Where(m => m.GuestId == g.Id && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered)
                        && m.Kind != MessageKind.Test)
            .ToListAsync(ct);
        foreach (var m in arrived)
        {
            m.Status = MessageStatus.Opened;
            m.OpenedAtUtc ??= clock.UtcNow;
            m.DeliveredAtUtc ??= clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> IsClosedAsync(CancellationToken ct = default) =>
        (await settings.GetAsync(ct)).StartsAtLocal is not { } startsAt || clock.LocalNow >= startsAt;

    /// <summary>
    /// Records an answer. Attending needs at least one adult; adults and children together must fit the invitation
    /// (<see cref="Guest.MaxHeadcount"/>). Children are stored separately and included in AttendingCount.
    /// </summary>
    public async Task<RsvpResult> SubmitAsync(string token, bool? attending, int adults, int children, CancellationToken ct = default)
    {
        var g = await FindAsync(token, ct);
        if (g is null) return RsvpResult.NotFound;
        if (await IsClosedAsync(ct)) return RsvpResult.Closed;
        if (attending is null) return RsvpResult.MissingAnswer;
        if (attending == true && (adults < 1 || children < 0 || adults + children > g.MaxHeadcount)) return RsvpResult.InvalidCount;

        g.Status = attending.Value ? RsvpStatus.Attending : RsvpStatus.Declined;
        g.AttendingCount = attending.Value ? adults + children : 0;
        g.AttendingChildren = attending.Value ? children : 0;
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
