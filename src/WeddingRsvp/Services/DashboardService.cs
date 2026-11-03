using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public sealed record DashboardStats(
    int Invitations,
    int InvitedPeople,
    int UnlimitedInvitations,
    int Responded,
    int AttendingInvitations,
    int AttendingPeople,
    int AttendingAdults,
    int AttendingChildren,
    int DietVegetarian,
    int DietVegan,
    int DietGlutenFree,
    IReadOnlyList<Guest> DietNotes,
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
        // Every member of a family holds a copy of the family's answer: count the answer once, list each guest.
        var answers = attending.GroupBy(g => g.FamilyId is { } f ? "f" + f : "g" + g.Id).Select(x => x.First()).ToList();
        var pending = guests.Where(g => g.Status == RsvpStatus.Pending).ToList();

        return new DashboardStats(
            Invitations: guests.Count,
            InvitedPeople: guests.Sum(g => g.InvitedCount), // invitations with no limit add 0; they are counted below
            UnlimitedInvitations: guests.Count(g => g.NoGuestLimit),
            Responded: guests.Count - pending.Count,
            AttendingInvitations: attending.Count,
            AttendingPeople: answers.Sum(g => g.AttendingCount), // adults and children together
            AttendingAdults: answers.Sum(g => g.AttendingAdults),
            AttendingChildren: answers.Sum(g => g.AttendingChildren),
            DietVegetarian: answers.Sum(g => g.DietVegetarian),
            DietVegan: answers.Sum(g => g.DietVegan),
            DietGlutenFree: answers.Sum(g => g.DietGlutenFree),
            DietNotes: answers.Where(g => !string.IsNullOrWhiteSpace(g.DietNote)).ToList(),
            Declined: guests.Count(g => g.Status == RsvpStatus.Declined),
            Pending: pending.Count,
            PendingButOpened: pending.Count(g => g.FirstOpenedAtUtc != null),
            InvitesNotSent: guests.Count(g => g.InviteSentAtUtc == null && g.GroupLinkId == null),
            InvalidPhones: guests.Count(g => g.PhoneE164 == null && g.PhoneError != null),
            QueuedMessages: queued,
            FailedMessages: failed,
            Attending: attending);
    }
}
