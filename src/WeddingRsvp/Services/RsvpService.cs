using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Services;

public enum RsvpResult { Saved, NotFound, Closed, InvalidCount, MissingAnswer, InvalidDiet }

/// <summary>Dietary answer: how many of the people attending need each kind of meal, and a free note.</summary>
public sealed record DietaryAnswer(int Vegetarian, int Vegan, int GlutenFree, string? Note)
{
    /// <summary>Each count is 0..people attending and the note is short.</summary>
    public bool IsValid(int people) =>
        Vegetarian >= 0 && Vegan >= 0 && GlutenFree >= 0
        && Vegetarian <= people && Vegan <= people && GlutenFree <= people
        && (Note?.Trim().Length ?? 0) <= Guest.DietNoteMax;
}

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
        var g = await db.Guests.IgnoreQueryFilters().Include(x => x.Family).FirstOrDefaultAsync(x => x.Token == token, ct);
        if (g is not null)
        {
            tenant.Use(g.CoupleId);
            if (g.FamilyId is { } fid) g.FamilyInvited = await new FamilyService(db).InvitedAsync(fid, ct);
            return g;
        }

        // A couple's demo link (the one in test messages): a made-up guest that is never stored.
        var demo = await db.EventSettings.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.PreviewToken == token, ct);
        if (demo is null) return null;
        tenant.Use(demo.CoupleId);
        return new Guest { CoupleId = demo.CoupleId, Token = token, Name = "", InvitedCount = 4, Status = RsvpStatus.Pending, IsDemo = true };
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
        if (g.IsDemo) return true; // a test message's demo page is not a guest opening anything
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
    public async Task<RsvpResult> SubmitAsync(string token, bool? attending, int adults, int children, DietaryAnswer? diet = null, CancellationToken ct = default)
    {
        var g = await FindAsync(token, ct);
        if (g is null) return RsvpResult.NotFound;
        if (g.IsDemo) return RsvpResult.Saved; // nothing is stored for the demo guest (the page shows the answer back)
        if (await IsClosedAsync(ct)) return RsvpResult.Closed;
        if (attending is null) return RsvpResult.MissingAnswer;
        if (attending == true && (adults < 1 || children < 0 || (long)adults + children > g.MaxHeadcount)) return RsvpResult.InvalidCount;

        var askDiet = (await settings.GetAsync(ct)).AskDietary;
        if (askDiet && attending == true && diet is not null && !diet.IsValid(adults + children)) return RsvpResult.InvalidDiet;

        g.Status = attending.Value ? RsvpStatus.Attending : RsvpStatus.Declined;
        g.AttendingCount = attending.Value ? adults + children : 0;
        g.AttendingChildren = attending.Value ? children : 0;
        if (askDiet)
        {
            // A "no" clears the dietary answer; a "yes" with nothing sent means none.
            var d = attending == true ? diet : null;
            g.DietVegetarian = d?.Vegetarian ?? 0;
            g.DietVegan = d?.Vegan ?? 0;
            g.DietGlutenFree = d?.GlutenFree ?? 0;
            g.DietNote = string.IsNullOrWhiteSpace(d?.Note) ? null : d!.Note!.Trim();
        }
        g.Source = RsvpSource.Web;
        g.RespondedAtUtc = clock.UtcNow;

        // A family shares one answer: every member gets it, and none of them is reminded any more.
        await CancelQueuedRemindersAsync(g.Id, ct);
        if (g.FamilyId is { } fid)
        {
            var family = new FamilyService(db);
            await family.ShareAnswerAsync(g, ct);
            foreach (var memberId in await family.MemberIdsAsync(fid, ct)) await CancelQueuedRemindersAsync(memberId, ct);
        }
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
