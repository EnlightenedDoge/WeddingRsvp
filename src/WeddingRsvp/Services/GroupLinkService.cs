using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Services;

public enum GroupLookupKind
{
    /// <summary>The phone number is not valid.</summary>
    Invalid,
    /// <summary>Nobody with this number yet: show the form.</summary>
    New,
    /// <summary>This number already answered through a group link for itself: show the form filled in.</summary>
    Returning,
    /// <summary>Someone else added this number to their answer: show the "already counted" card (with the take-over option).</summary>
    CountedByOther,
    /// <summary>The number belongs to a guest from the couple's own list (personal link): point them to that link.</summary>
    OnList,
}

public sealed record GroupLookup(GroupLookupKind Kind, string? E164 = null, Guest? Existing = null, string? OwnerName = null);

/// <summary>One person named in an answer (besides the one answering). A child's phone is optional.</summary>
public sealed record GroupPerson(string First, string Last, string? Phone, bool IsChild);

/// <summary>A problem with one field of the form. <see cref="Field"/> is "me.first", "me.last", "me.phone", "adult[0].phone", "child[1].last", ...</summary>
public sealed record GroupFieldError(string Field, string Code);

public enum GroupSubmitKind { Saved, NotFound, Closed, Invalid, OnList, CountedByOther }

public sealed record GroupSubmitResult(GroupSubmitKind Kind, IReadOnlyList<GroupFieldError> Errors, IReadOnlyList<string> Names)
{
    public static GroupSubmitResult Of(GroupSubmitKind k) => new(k, [], []);
}

public sealed record GroupLinkRow(GroupLink Link, GroupLinkStatus Status, int Answers, int Guests);

public sealed record GroupResponse(Guest Who, IReadOnlyList<Guest> Added)
{
    public int People => Who.Status == RsvpStatus.Attending ? 1 + Added.Count : 0;
}

/// <summary>
/// Group links: one shared link for a whole group. Everyone who opens it gives a phone number and a name; whoever answers (and the
/// people they add) becomes a guest of the couple. The phone number is the identity: the same number later reopens the same answer.
/// No SMS code check yet (planned), so anyone who knows a number can answer for it.
/// </summary>
public sealed class GroupLinkService(AppDbContext db, SettingsService settings, RsvpService rsvp, IsraelClock clock, ITenantContext tenant)
{
    public const int MaxNameLength = 100;

    // ---------- admin (couple session; the tenant filter applies) ----------

    public GroupLinkStatus StatusOf(GroupLink l, EventSettings s)
    {
        if (l.IsOff) return GroupLinkStatus.Off;
        var now = clock.LocalNow;
        if (l.ExpiresAtLocal is { } exp ? now >= exp : s.StartsAtLocal is not { } start || now >= start) return GroupLinkStatus.Expired;
        return GroupLinkStatus.Active;
    }

    public async Task<List<GroupLinkRow>> ListAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var links = await db.GroupLinks.AsNoTracking().OrderByDescending(l => l.CreatedAtUtc).ToListAsync(ct);
        var members = await db.Guests.AsNoTracking().Where(g => g.GroupLinkId != null)
            .Select(g => new { g.GroupLinkId, g.AddedByGuestId, g.Status, g.AttendingCount }).ToListAsync(ct);
        return links.Select(l =>
        {
            var mine = members.Where(m => m.GroupLinkId == l.Id).ToList();
            return new GroupLinkRow(l, StatusOf(l, s), mine.Count(m => m.AddedByGuestId == null), mine.Sum(m => m.AttendingCount));
        }).ToList();
    }

    public Task<GroupLink?> GetAsync(int id, CancellationToken ct = default) =>
        db.GroupLinks.FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<List<GroupResponse>> ResponsesAsync(int linkId, CancellationToken ct = default)
    {
        var all = await db.Guests.AsNoTracking().Where(g => g.GroupLinkId == linkId).ToListAsync(ct);
        return all.Where(g => g.AddedByGuestId == null)
            .OrderByDescending(g => g.RespondedAtUtc)
            .Select(g => new GroupResponse(g, all.Where(a => a.AddedByGuestId == g.Id).OrderBy(a => a.IsChild).ThenBy(a => a.Id).ToList()))
            .ToList();
    }

    /// <summary>Checks the fields of the create/edit form. Returns an error message key, or null when fine.</summary>
    public static string? Validate(string? name, int maxGuests) =>
        string.IsNullOrWhiteSpace(name) ? "name"
        : name.Trim().Length > MaxNameLength ? "name"
        : maxGuests is < 0 or > GroupLink.MaxGuestsLimit ? "max"
        : null;

    public async Task<GroupLink> CreateAsync(string name, int maxGuests, DateTime? expiresLocal, GroupVerifyMode verify = GroupVerifyMode.Off, CancellationToken ct = default)
    {
        var coupleId = tenant.CoupleId ?? throw new InvalidOperationException("No tenant is set.");
        string code;
        do code = TokenGenerator.NewGroupCode();
        while (await db.GroupLinks.IgnoreQueryFilters().AnyAsync(x => x.Code == code, ct));
        var l = new GroupLink
        {
            CoupleId = coupleId, Name = name.Trim(), Code = code, MaxGuests = maxGuests,
            ExpiresAtLocal = expiresLocal, VerifyMode = Enum.IsDefined(verify) ? verify : GroupVerifyMode.Off, CreatedAtUtc = clock.UtcNow,
        };
        db.GroupLinks.Add(l);
        await db.SaveChangesAsync(ct);
        return l;
    }

    public async Task<bool> UpdateAsync(int id, string name, int maxGuests, DateTime? expiresLocal, GroupVerifyMode? verify = null, CancellationToken ct = default)
    {
        var l = await GetAsync(id, ct);
        if (l is null) return false;
        l.Name = name.Trim();
        l.MaxGuests = maxGuests;
        l.ExpiresAtLocal = expiresLocal;
        if (verify is { } v && Enum.IsDefined(v)) l.VerifyMode = v;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetOffAsync(int id, bool off, CancellationToken ct = default)
    {
        var l = await GetAsync(id, ct);
        if (l is null) return false;
        l.IsOff = off;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- public (no session; the code picks the couple) ----------

    /// <summary>
    /// The one place the group flow leaves tenant scope: the code is unguessable and decides which couple this request acts for.
    /// After this call all queries are scoped to that couple.
    /// </summary>
    public async Task<GroupLink?> FindAsync(string code, CancellationToken ct = default)
    {
        if (!TokenGenerator.LooksValidGroupCode(code)) return null;
        var l = await db.GroupLinks.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Code == code, ct);
        if (l is not null) tenant.Use(l.CoupleId);
        return l;
    }

    /// <summary>True when answers through this link are not accepted (turned off, expired, or the event has started).</summary>
    public async Task<bool> IsClosedAsync(GroupLink l, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        return StatusOf(l, s) != GroupLinkStatus.Active || await rsvp.IsClosedAsync(ct);
    }

    /// <summary>What to show after a visitor enters a phone number.</summary>
    public async Task<GroupLookup> LookupAsync(string? phone, CancellationToken ct = default)
    {
        var p = PhoneNormalizer.Normalize(phone);
        if (!p.IsValid) return new(GroupLookupKind.Invalid);
        var existing = await db.Guests.OrderBy(g => g.Id).FirstOrDefaultAsync(g => g.PhoneE164 == p.E164, ct);
        if (existing is null) return new(GroupLookupKind.New, p.E164);
        if (existing.GroupLinkId is null) return new(GroupLookupKind.OnList, p.E164, existing);
        if (existing.AddedByGuestId is { } by)
        {
            var owner = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == by, ct);
            return new(GroupLookupKind.CountedByOther, p.E164, existing, owner?.Name);
        }
        return new(GroupLookupKind.Returning, p.E164, existing);
    }

    /// <summary>The people someone added to their answer, adults first.</summary>
    public Task<List<Guest>> AddedByAsync(int ownerId, CancellationToken ct = default) =>
        db.Guests.AsNoTracking().Where(g => g.AddedByGuestId == ownerId).OrderBy(g => g.IsChild).ThenBy(g => g.Id).ToListAsync(ct);

    /// <summary>
    /// Saves an answer. The person answering is always an adult with a phone; everyone they add needs a first and last name, adults a
    /// phone, children optionally one. A phone already used by someone outside this answer is refused (nobody is counted twice).
    /// Reopening with the same phone replaces the earlier answer; someone another person added can <paramref name="takeover"/> and
    /// answer for themselves (they leave the other person's list).
    /// </summary>
    public async Task<GroupSubmitResult> SubmitAsync(
        string code, string? first, string? last, string? phone, bool attending, IReadOnlyList<GroupPerson> others, bool takeover,
        CancellationToken ct = default)
    {
        var link = await FindAsync(code, ct);
        if (link is null) return GroupSubmitResult.Of(GroupSubmitKind.NotFound);
        if (await IsClosedAsync(link, ct)) return GroupSubmitResult.Of(GroupSubmitKind.Closed);
        return await SubmitAsync(link, first, last, phone, attending, others, takeover, dryRun: false, ct);
    }

    /// <param name="dryRun">The couple's preview: everything is checked against the real list, nothing is stored.</param>
    public async Task<GroupSubmitResult> SubmitAsync(
        GroupLink link, string? first, string? last, string? phone, bool attending, IReadOnlyList<GroupPerson> others, bool takeover, bool dryRun,
        CancellationToken ct = default)
    {

        var errors = new List<GroupFieldError>();
        first = first?.Trim() ?? ""; last = last?.Trim() ?? "";
        if (first.Length == 0 || first.Length > MaxNameLength) errors.Add(new("me.first", "name"));
        if (last.Length == 0 || last.Length > MaxNameLength) errors.Add(new("me.last", "name"));
        var me = PhoneNormalizer.Normalize(phone);
        if (!me.IsValid) errors.Add(new("me.phone", "phone"));
        if (!me.IsValid) return new(GroupSubmitKind.Invalid, errors, []);

        var existing = await db.Guests.OrderBy(g => g.Id).FirstOrDefaultAsync(g => g.PhoneE164 == me.E164, ct);
        if (existing is not null && existing.GroupLinkId is null) return GroupSubmitResult.Of(GroupSubmitKind.OnList);
        if (existing?.AddedByGuestId is not null && !takeover) return GroupSubmitResult.Of(GroupSubmitKind.CountedByOther);

        // Declining covers only the person answering.
        var people = attending ? others : [];
        if (1 + people.Count > link.EffectiveMax) errors.Add(new("count", "max"));

        var seen = new HashSet<string> { me.E164! };
        var adultIdx = 0; var childIdx = 0;
        var checkedPeople = new List<(GroupPerson P, string? E164, string Raw)>();
        foreach (var p in people)
        {
            var key = (p.IsChild ? "child" : "adult") + "[" + (p.IsChild ? childIdx++ : adultIdx++) + "]";
            var pf = p.First?.Trim() ?? ""; var pl = p.Last?.Trim() ?? "";
            if (pf.Length == 0 || pf.Length > MaxNameLength) errors.Add(new(key + ".first", "name"));
            if (pl.Length == 0 || pl.Length > MaxNameLength) errors.Add(new(key + ".last", "name"));
            string? e164 = null;
            if (!string.IsNullOrWhiteSpace(p.Phone))
            {
                var n = PhoneNormalizer.Normalize(p.Phone);
                if (!n.IsValid) errors.Add(new(key + ".phone", "phone"));
                else if (!seen.Add(n.E164!)) errors.Add(new(key + ".phone", "duplicate"));
                else e164 = n.E164;
            }
            else if (!p.IsChild) errors.Add(new(key + ".phone", "phone"));
            checkedPeople.Add((p with { First = pf, Last = pl }, e164, p.Phone?.Trim() ?? ""));
        }

        // A phone someone else already has is refused, unless it belongs to this very answer (it is replaced below).
        var phones = checkedPeople.Where(c => c.E164 is not null).Select(c => c.E164!).ToList();
        if (phones.Count > 0)
        {
            var selfId = existing?.Id ?? -1;
            var mineIds = await db.Guests.Where(g => g.AddedByGuestId == selfId).Select(g => g.Id).ToListAsync(ct);
            var taken = await db.Guests.AsNoTracking()
                .Where(g => phones.Contains(g.PhoneE164!) && g.Id != selfId && !mineIds.Contains(g.Id))
                .Select(g => g.PhoneE164!).ToListAsync(ct);
            var adultI = 0; var childI = 0;
            foreach (var c in checkedPeople)
            {
                var key = (c.P.IsChild ? "child" : "adult") + "[" + (c.P.IsChild ? childI++ : adultI++) + "]";
                if (c.E164 is not null && taken.Contains(c.E164) && !errors.Any(e => e.Field == key + ".phone"))
                    errors.Add(new(key + ".phone", "duplicate"));
            }
        }
        if (errors.Count > 0) return new(GroupSubmitKind.Invalid, errors, []);

        if (dryRun)
        {
            var preview = new List<string>();
            if (attending) { preview.Add(first + " " + last); preview.AddRange(checkedPeople.Select(c => c.P.First + " " + c.P.Last)); }
            return new(GroupSubmitKind.Saved, [], preview);
        }

        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var who = existing;
        if (who is null)
        {
            who = new Guest
            {
                CoupleId = link.CoupleId, GroupLinkId = link.Id, Token = await PreviewTokens.NewAsync(db, ct),
                PhoneRaw = phone!.Trim(), PhoneE164 = me.E164, CreatedAtUtc = now, InvitedCount = 1,
            };
            db.Guests.Add(who);
        }
        who.Name = first + " " + last;
        who.AddedByGuestId = null; // taking over: leave the other person's list
        who.IsChild = false;
        who.InvitedCount = 1;
        who.Status = attending ? RsvpStatus.Attending : RsvpStatus.Declined;
        who.AttendingCount = attending ? 1 : 0;
        who.AttendingChildren = 0;
        who.Source = RsvpSource.Web;
        who.RespondedAtUtc = now;
        await db.SaveChangesAsync(ct); // gives a new guest its Id

        // The people added earlier are replaced by the ones in this answer.
        var old = await db.Guests.Where(g => g.AddedByGuestId == who.Id).ToListAsync(ct);
        db.Guests.RemoveRange(old);
        foreach (var (p, e164, raw) in checkedPeople)
        {
            db.Guests.Add(new Guest
            {
                CoupleId = link.CoupleId, GroupLinkId = link.Id, AddedByGuestId = who.Id, IsChild = p.IsChild,
                Name = p.First + " " + p.Last, Token = await PreviewTokens.NewAsync(db, ct),
                PhoneRaw = raw, PhoneE164 = e164, PhoneError = null, // a child without a phone is not an "invalid number"
                InvitedCount = 1, Status = RsvpStatus.Attending, AttendingCount = 1, AttendingChildren = p.IsChild ? 1 : 0,
                Source = RsvpSource.Web, RespondedAtUtc = now, CreatedAtUtc = now,
            });
            await db.SaveChangesAsync(ct); // one at a time: each token is checked against the ones already saved
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var names = new List<string>();
        if (attending) { names.Add(who.Name); names.AddRange(checkedPeople.Select(c => c.P.First + " " + c.P.Last)); }
        return new(GroupSubmitKind.Saved, [], names);
    }

    /// <summary>"Noa Levi" -> ("Noa", "Levi"): the first word is the first name, the rest the last name.</summary>
    public static (string First, string Last) SplitName(string? name)
    {
        var s = (name ?? "").Trim();
        var i = s.IndexOf(' ');
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..].Trim());
    }
}
