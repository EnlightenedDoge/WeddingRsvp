using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public sealed record BulkInviteResult(int Queued, int NoPhone, int AlreadyInvited, int GroupLink, IReadOnlyList<string> MissingFields);

/// <summary>Actions on several selected guests at once (the Guests list). Everything is scoped to the signed-in couple by the query filters.</summary>
public sealed class GuestBulkService(AppDbContext db, CampaignService campaign, FamilyService families, IsraelClock clock)
{
    /// <summary>
    /// Queues the invitation for the selected guests who have a valid number and have not been invited yet. Someone already invited is
    /// skipped (use the guest's own page to send again on purpose), as is anyone who answered through a group link. Nothing is queued
    /// while an event detail the invitation uses is missing.
    /// </summary>
    public async Task<BulkInviteResult> SendInvitesAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var missing = await campaign.MissingFieldsAsync(MessageKind.Invite, ct);
        if (missing.Count > 0) return new(0, 0, 0, 0, missing);
        int queued = 0, noPhone = 0, already = 0, group = 0;
        foreach (var g in await db.Guests.AsNoTracking().Where(g => ids.Contains(g.Id)).ToListAsync(ct))
        {
            if (g.GroupLinkId != null) group++;
            else if (!g.HasValidPhone) noPhone++;
            else if (g.InviteSentAtUtc != null) already++;
            else if (await campaign.QueueInviteAsync(g.Id, ct)) queued++;
        }
        return new(queued, noPhone, already, group, []);
    }

    /// <summary>Sets how many people the selected invitations cover: 1..50, or 0 for no limit. Returns how many guests changed.</summary>
    public async Task<int> SetInvitedAsync(IReadOnlyCollection<int> ids, int invited, CancellationToken ct = default)
    {
        if (invited is < 0 or > 50) return -1;
        var guests = await db.Guests.Where(g => ids.Contains(g.Id)).ToListAsync(ct);
        foreach (var g in guests) g.InvitedCount = invited;
        await db.SaveChangesAsync(ct);
        return guests.Count;
    }

    /// <summary>
    /// Puts the selected guests in one family (<paramref name="familyId"/> = an existing one, or <see cref="FamilyService.NewFamily"/> with a name),
    /// or takes them out of their families (<see cref="FamilyService.None"/>). When several of them have already answered, the most recent
    /// answer is the one the family keeps. Returns how many guests were handled, or -1 when a new family has no name or the family is unknown.
    /// </summary>
    public async Task<int> SetFamilyAsync(IReadOnlyCollection<int> ids, int familyId, string? name, CancellationToken ct = default)
    {
        var guests = (await db.Guests.Where(g => ids.Contains(g.Id)).ToListAsync(ct))
            .OrderByDescending(g => g.RespondedAtUtc ?? DateTime.MinValue).ToList(); // most recent answer first, so it wins
        if (familyId == FamilyService.NewFamily)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            var fam = db.Families.Add(new Family { Name = name.Trim()[..Math.Min(name.Trim().Length, Family.NameMax)], CreatedAtUtc = clock.UtcNow }).Entity;
            await db.SaveChangesAsync(ct);
            familyId = fam.Id;
            name = null;
        }
        foreach (var g in guests)
        {
            if (!await families.SetFamilyAsync(g, familyId, name, clock.UtcNow, ct)) return -1;
            await db.SaveChangesAsync(ct); // one at a time: "is the old family empty now" and "who has answered" read the saved state
        }
        return guests.Count;
    }

    /// <summary>Deletes the selected guests. Their queued messages are cancelled (a deleted guest must not still be messaged) and families left empty are removed.</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var guests = await db.Guests.Where(g => ids.Contains(g.Id)).ToListAsync(ct);
        var gone = guests.Select(g => g.Id).ToList();
        foreach (var m in await db.Messages.Where(m => m.GuestId != null && gone.Contains(m.GuestId!.Value) && m.Status == MessageStatus.Queued).ToListAsync(ct))
        {
            m.Status = MessageStatus.Cancelled;
            m.Error = "Guest deleted";
        }
        var familyIds = guests.Where(g => g.FamilyId != null).Select(g => g.FamilyId!.Value).Distinct().ToList();
        db.Guests.RemoveRange(guests);
        await db.SaveChangesAsync(ct);
        foreach (var fid in familyIds)
            if (!await db.Guests.AnyAsync(g => g.FamilyId == fid, ct))
                db.Families.RemoveRange(await db.Families.Where(f => f.Id == fid).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return guests.Count;
    }
}
