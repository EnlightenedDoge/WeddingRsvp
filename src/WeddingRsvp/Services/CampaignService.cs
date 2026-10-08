using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;

namespace WeddingRsvp.Services;

/// <summary>
/// Decides WHAT should be sent and puts it in the outbox (MessageLog rows with Status=Queued).
/// MessageDispatcher decides WHEN and actually sends. Every method here is idempotent:
/// a guest never has two queued messages of the same kind.
/// </summary>
public sealed class CampaignService(
    AppDbContext db, SettingsService settings, IsraelClock clock, IOptions<AppOptions> app,
    ILogger<CampaignService> log)
{
    /// <summary>
    /// Event details the given message template uses but the couple hasn't filled in yet, e.g. "{couple}".
    /// While this is non-empty nothing of that kind is queued: a guest must never receive "ב- !" instead of a date.
    /// </summary>
    public async Task<IReadOnlyList<string>> MissingFieldsAsync(MessageKind kind, CancellationToken ct = default) =>
        MessageTemplates.MissingFields(await settings.GetAsync(ct), kind);

    /// <summary>Queues invitations for every valid guest that hasn't been invited yet.</summary>
    public async Task<int> QueueInvitesAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        if (MessageTemplates.MissingFields(s, MessageKind.Invite).Count > 0) return 0;
        var queued = await QueuedGuestIdsAsync(MessageKind.Invite, ct);
        var guests = await db.Guests
            .Where(g => g.PhoneE164 != null && g.InviteSentAtUtc == null)
            .ToListAsync(ct);

        var n = 0;
        foreach (var g in guests.Where(g => !queued.Contains(g.Id)))
        {
            Enqueue(g, MessageKind.Invite, s);
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>Queues (or re-queues) one guest's invitation, e.g. after fixing their number.</summary>
    public async Task<bool> QueueInviteAsync(int guestId, CancellationToken ct = default)
    {
        var g = await db.Guests.FindAsync([guestId], ct);
        if (g is null || !g.HasValidPhone) return false;
        var s = await settings.GetAsync(ct);
        if (MessageTemplates.MissingFields(s, MessageKind.Invite).Count > 0) return false;
        if ((await QueuedGuestIdsAsync(MessageKind.Invite, ct)).Contains(g.Id)) return true;
        Enqueue(g, MessageKind.Invite, s);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sends a rendered template to an arbitrary number so the couple can see the real thing.</summary>
    public async Task<bool> QueueTestAsync(string phone, MessageKind template, CancellationToken ct = default)
    {
        var p = PhoneNormalizer.Normalize(phone);
        if (!p.IsValid) return false;
        var s = await settings.GetAsync(ct);
        var link = MessageTemplates.GuestLink(app.Value.PublicBaseUrl, "TESTTESTAB");
        db.Messages.Add(new MessageLog
        {
            Kind = MessageKind.Test,
            ToE164 = p.E164!,
            Body = MessageTemplates.Render(MessageTemplates.TemplateFor(s, template), "ישראל ישראלי", s, link),
            CreatedAtUtc = clock.UtcNow,
            NotBeforeUtc = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RequeueFailedAsync(CancellationToken ct = default)
    {
        var failed = await db.Messages.Where(m => m.Status == MessageStatus.Failed).ToListAsync(ct);
        foreach (var m in failed)
        {
            m.Status = MessageStatus.Queued;
            m.Attempts = 0;
            m.NotBeforeUtc = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return failed.Count;
    }

    /// <summary>Called by the background worker every tick: automatic invites, reminders, thank-yous.</summary>
    public async Task PlanAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var local = clock.LocalNow;
        if (s.StartsAtLocal is not { } startsAt) return; // no event date yet: nothing can be planned

        if (s.AutoInviteAtLocal is { } autoAt && local >= autoAt && local < startsAt)
        {
            var n = await QueueInvitesAsync(ct);
            if (n > 0) log.LogInformation("Auto-queued {Count} invitations", n);
        }

        if (s.RemindersEnabled && local < startsAt
            && MessageTemplates.MissingFields(s, MessageKind.Reminder).Count == 0)
            await PlanRemindersAsync(s, ct);

        if (s.ThankYouEnabled && MessageTemplates.MissingFields(s, MessageKind.ThankYou).Count == 0)
        {
            var sendFrom = startsAt.Date.AddDays(1).AddHours(s.ThankYouHourLocal);
            if (local >= sendFrom && local < sendFrom.AddDays(2)) // don't fire weeks later after downtime
                await PlanThankYousAsync(s, ct);
        }
    }

    private async Task PlanRemindersAsync(EventSettings s, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var interval = TimeSpan.FromDays(s.ReminderIntervalDays);
        var queued = await QueuedGuestIdsAsync(MessageKind.Reminder, ct);
        var candidates = await db.Guests
            .Where(g => g.Status == RsvpStatus.Pending && g.PhoneE164 != null
                        && g.InviteSentAtUtc != null && g.RemindersSent < s.MaxReminders)
            .ToListAsync(ct);

        var n = 0;
        foreach (var g in candidates)
        {
            var last = g.LastReminderAtUtc ?? g.InviteSentAtUtc!.Value;
            if (now - last < interval || queued.Contains(g.Id)) continue;
            Enqueue(g, MessageKind.Reminder, s);
            n++;
        }
        if (n == 0) return;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Queued {Count} reminders", n);
    }

    private async Task PlanThankYousAsync(EventSettings s, CancellationToken ct)
    {
        var queued = await QueuedGuestIdsAsync(MessageKind.ThankYou, ct);
        var q = db.Guests.Where(g => g.PhoneE164 != null && g.ThankYouSentAtUtc == null);
        q = s.ThankYouAudience == ThankYouAudience.AttendingOnly
            ? q.Where(g => g.Status == RsvpStatus.Attending)
            : q.Where(g => g.InviteSentAtUtc != null);

        var n = 0;
        foreach (var g in (await q.ToListAsync(ct)).Where(g => !queued.Contains(g.Id)))
        {
            Enqueue(g, MessageKind.ThankYou, s);
            n++;
        }
        if (n == 0) return;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Queued {Count} thank-you messages", n);
    }

    private void Enqueue(Guest g, MessageKind kind, EventSettings s)
    {
        var link = MessageTemplates.GuestLink(app.Value.PublicBaseUrl, g.Token);
        db.Messages.Add(new MessageLog
        {
            GuestId = g.Id,
            Kind = kind,
            ToE164 = g.PhoneE164!,
            Body = MessageTemplates.Render(MessageTemplates.TemplateFor(s, kind), g.Name, s, link),
            CreatedAtUtc = clock.UtcNow,
            NotBeforeUtc = clock.UtcNow,
        });
    }

    private async Task<HashSet<int>> QueuedGuestIdsAsync(MessageKind kind, CancellationToken ct) =>
        (await db.Messages
            .Where(m => m.Kind == kind && m.Status == MessageStatus.Queued && m.GuestId != null)
            .Select(m => m.GuestId!.Value)
            .ToListAsync(ct)).ToHashSet();
}
