using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

/// <summary>
/// Families: guests who share one answer. Every member keeps their own row, phone, link and SMS, and holds a copy of the family's
/// answer (status, headcount, dietary needs), so a member's page and the lists read their own row. Totals count a family once
/// (see <see cref="DashboardService"/>).
/// </summary>
public enum AttendanceEdit { Saved, NotFound, NotAttending, InvalidCount, InvalidDiet }

public sealed class FamilyService(AppDbContext db)
{
    public async Task<List<Family>> ListAsync(CancellationToken ct = default) =>
        await db.Families.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);

    /// <summary>A family with who is in it, for pickers (names can repeat, so the members tell two Cohen families apart).</summary>
    public sealed record FamilyOption(int Id, string Name, string Members);

    public async Task<List<FamilyOption>> OptionsAsync(CancellationToken ct = default)
    {
        var families = await db.Families.AsNoTracking().OrderBy(f => f.Name).ThenBy(f => f.Id).ToListAsync(ct);
        var members = (await db.Guests.AsNoTracking().Where(g => g.FamilyId != null).Select(g => new { g.FamilyId, g.Name }).ToListAsync(ct))
            .ToLookup(x => x.FamilyId!.Value, x => x.Name);
        return families.Select(f => new FamilyOption(f.Id, f.Name, string.Join(", ", members[f.Id].Take(4)))).ToList();
    }

    public const int None = 0, NewFamily = -1;

    /// <summary>
    /// Sets the guest's family. <paramref name="familyId"/>: 0 = none, -1 = a new family called <paramref name="name"/>, otherwise that
    /// family (renamed when a different name is given). Joining a family that already has an answer adopts it; a guest who already
    /// answered passes the answer on when nobody else has. A family left empty is deleted. Does not save; returns false when the
    /// family does not exist or a new family has no name.
    /// </summary>
    public async Task<bool> SetFamilyAsync(Guest g, int familyId, string? name, DateTime nowUtc, CancellationToken ct = default)
    {
        name = name?.Trim();
        if (name is { Length: > Family.NameMax }) name = name[..Family.NameMax];
        var oldId = g.FamilyId;
        if (familyId == None) { g.FamilyId = null; g.Family = null; }
        else if (familyId == NewFamily)
        {
            if (string.IsNullOrEmpty(name)) return false;
            g.Family = db.Families.Add(new Family { Name = name, CreatedAtUtc = nowUtc }).Entity;
        }
        else
        {
            var fam = await db.Families.FirstOrDefaultAsync(f => f.Id == familyId, ct);
            if (fam is null) return false;
            if (!string.IsNullOrEmpty(name)) fam.Name = name;
            if (g.FamilyId != fam.Id) { g.Family = fam; await AdoptAnswerAsync(g, fam.Id, ct); }
        }
        if (oldId is { } old && old != g.FamilyId) await DeleteIfEmptyAsync(old, g.Id, ct);
        return true;
    }

    /// <summary>A new member takes the answer of the family's most recent responder; with no answer in the family theirs stays.</summary>
    public async Task AdoptAnswerAsync(Guest g, int familyId, CancellationToken ct = default)
    {
        var source = await db.Guests.Where(x => x.FamilyId == familyId && x.Id != g.Id && x.RespondedAtUtc != null)
            .OrderByDescending(x => x.RespondedAtUtc).FirstOrDefaultAsync(ct);
        if (source is not null) CopyAnswer(source, g);
        else if (g.RespondedAtUtc is not null)
            foreach (var other in await db.Guests.Where(x => x.FamilyId == familyId && x.Id != g.Id).ToListAsync(ct)) CopyAnswer(g, other);
    }

    private async Task DeleteIfEmptyAsync(int familyId, int leavingGuestId, CancellationToken ct)
    {
        if (await db.Guests.AnyAsync(x => x.FamilyId == familyId && x.Id != leavingGuestId, ct)) return;
        var fam = await db.Families.FirstOrDefaultAsync(f => f.Id == familyId, ct);
        if (fam is not null) db.Families.Remove(fam);
    }

    /// <summary>Copies the guest's answer to every other member of their family (no-op for a guest on their own). Does not save.</summary>
    public async Task ShareAnswerAsync(Guest source, CancellationToken ct = default)
    {
        if (source.FamilyId is not { } fid) return;
        foreach (var m in await db.Guests.Where(x => x.FamilyId == fid && x.Id != source.Id).ToListAsync(ct)) CopyAnswer(source, m);
    }

    /// <summary>Everyone the family's members are invited with together: 0 when any member has no limit, else the sum.</summary>
    public async Task<int> InvitedAsync(int familyId, CancellationToken ct = default)
    {
        var counts = await db.Guests.Where(x => x.FamilyId == familyId).Select(x => x.InvitedCount).ToListAsync(ct);
        return counts.Contains(0) ? 0 : counts.Sum();
    }

    /// <summary>Queued reminders of every member are cancelled once any of them has answered (done by the caller).</summary>
    public async Task<List<int>> MemberIdsAsync(int familyId, CancellationToken ct = default) =>
        await db.Guests.Where(x => x.FamilyId == familyId).Select(x => x.Id).ToListAsync(ct);

    /// <summary>
    /// The couple corrects how many people are attending for a guest who has said yes, and their dietary needs (the quick edit on the lists).
    /// Recorded as a manual answer; the limit is the guest's invitation (the whole family's for a family member), and a family's members
    /// all get the new answer.
    /// </summary>
    public async Task<AttendanceEdit> EditAttendanceAsync(int guestId, int adults, int children, DietaryAnswer diet, DateTime nowUtc, CancellationToken ct = default)
    {
        var g = await db.Guests.FirstOrDefaultAsync(x => x.Id == guestId, ct);
        if (g is null) return AttendanceEdit.NotFound;
        if (g.Status != RsvpStatus.Attending) return AttendanceEdit.NotAttending;
        var invited = g.FamilyId is { } fid ? await InvitedAsync(fid, ct) : g.InvitedCount;
        var max = invited > 0 ? invited : Guest.UnlimitedCap;
        if (adults < 1 || children < 0 || (long)adults + children > max) return AttendanceEdit.InvalidCount;
        if (!diet.IsValid(adults + children)) return AttendanceEdit.InvalidDiet;

        g.AttendingCount = adults + children;
        g.AttendingChildren = children;
        g.DietVegetarian = diet.Vegetarian; g.DietVegan = diet.Vegan; g.DietGlutenFree = diet.GlutenFree;
        g.DietNote = string.IsNullOrWhiteSpace(diet.Note) ? null : diet.Note.Trim();
        g.Source = RsvpSource.Manual;
        g.RespondedAtUtc = nowUtc;
        await ShareAnswerAsync(g, ct);
        await db.SaveChangesAsync(ct);
        return AttendanceEdit.Saved;
    }

    private static void CopyAnswer(Guest from, Guest to)
    {
        to.Status = from.Status;
        to.AttendingCount = from.AttendingCount;
        to.AttendingChildren = from.AttendingChildren;
        to.Source = from.Source;
        to.RespondedAtUtc = from.RespondedAtUtc;
        to.DietVegetarian = from.DietVegetarian;
        to.DietVegan = from.DietVegan;
        to.DietGlutenFree = from.DietGlutenFree;
        to.DietNote = from.DietNote;
    }
}
