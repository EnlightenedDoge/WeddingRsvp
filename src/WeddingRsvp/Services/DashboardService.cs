using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public sealed record DashboardStats(
    int Invitations,
    int InvitedPeople,
    int Responded,
    int AttendingInvitations,
    int AttendingPeople,
    int Declined,
    int Pending,
    int PendingButOpened,
    int InvitesNotSent,
    int InvalidPhones,
    int QueuedMessages,
    int FailedMessages,
    IReadOnlyList<Guest> Attending);

public sealed class DashboardService(AppDbContext db)
{
    public async Task<DashboardStats> GetAsync(CancellationToken ct = default)
    {
        var guests = await db.Guests.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var queued = await db.Messages.CountAsync(m => m.Status == MessageStatus.Queued, ct);
        var failed = await db.Messages.CountAsync(m => m.Status == MessageStatus.Failed, ct);

        var attending = guests.Where(g => g.Status == RsvpStatus.Attending).ToList();
        var pending = guests.Where(g => g.Status == RsvpStatus.Pending).ToList();

        return new DashboardStats(
            Invitations: guests.Count,
            InvitedPeople: guests.Sum(g => g.InvitedCount),
            Responded: guests.Count - pending.Count,
            AttendingInvitations: attending.Count,
            AttendingPeople: attending.Sum(g => g.AttendingCount),
            Declined: guests.Count(g => g.Status == RsvpStatus.Declined),
            Pending: pending.Count,
            PendingButOpened: pending.Count(g => g.FirstOpenedAtUtc != null),
            InvitesNotSent: guests.Count(g => g.InviteSentAtUtc == null),
            InvalidPhones: guests.Count(g => g.PhoneE164 == null),
            QueuedMessages: queued,
            FailedMessages: failed,
            Attending: attending);
    }
}
