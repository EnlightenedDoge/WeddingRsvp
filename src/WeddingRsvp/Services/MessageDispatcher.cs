using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services.Sms;

namespace WeddingRsvp.Services;

/// <summary>Drains the outbox, respecting the pause switch, the sending window and the batch throttle.</summary>
public sealed class MessageDispatcher(
    AppDbContext db, SettingsService settings, ISmsSender sender, SendingWindow window,
    IsraelClock clock, IOptions<SendingOptions> options, ILogger<MessageDispatcher> log)
{
    public async Task<int> DispatchAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        if (s.SendingPaused || !window.IsAllowed(clock.LocalNow)) return 0;

        var now = clock.UtcNow;
        var batch = await db.Messages
            .Include(m => m.Guest)
            .Where(m => m.Status == MessageStatus.Queued && m.NotBeforeUtc <= now)
            .OrderBy(m => m.Id)
            .Take(options.Value.BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var m in batch)
        {
            if (ShouldCancel(m) is { } reason)
            {
                m.Status = MessageStatus.Cancelled;
                m.Error = reason;
                await db.SaveChangesAsync(ct);
                continue;
            }

            m.Attempts++;
            SmsSendResult result;
            try
            {
                result = await sender.SendAsync(new SmsMessage(m.ToE164, m.Body, NullIfEmpty(s.SmsSenderName)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = SmsSendResult.Fail(ex.Message);
            }

            if (result.Success)
            {
                m.Status = MessageStatus.Sent;
                m.SentAtUtc = clock.UtcNow;
                m.ProviderMessageId = result.ProviderMessageId;
                m.Error = null;
                ApplyToGuest(m);
                sent++;
            }
            else if (m.Attempts < options.Value.MaxAttempts)
            {
                m.Error = result.Error;
                m.NotBeforeUtc = clock.UtcNow.AddMinutes(5 * m.Attempts);
                log.LogWarning("Send failed (attempt {Attempt}) for message {Id}: {Error}", m.Attempts, m.Id, result.Error);
            }
            else
            {
                m.Status = MessageStatus.Failed;
                m.Error = result.Error;
                log.LogError("Giving up on message {Id}: {Error}", m.Id, result.Error);
            }

            // Save per message so a crash mid-batch never re-sends what already went out.
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }

    private static string? ShouldCancel(MessageLog m) => (m.Kind, m.Guest) switch
    {
        (MessageKind.Test, _) => null,
        (_, null) => "Guest deleted",
        (MessageKind.Reminder, { Status: not RsvpStatus.Pending }) => "Guest responded",
        (MessageKind.ThankYou, { ThankYouSentAtUtc: not null }) => "Already thanked",
        _ => null,
    };

    private void ApplyToGuest(MessageLog m)
    {
        var g = m.Guest;
        if (g is null) return;
        switch (m.Kind)
        {
            case MessageKind.Invite:
                g.InviteSentAtUtc ??= m.SentAtUtc;
                break;
            case MessageKind.Reminder:
                g.RemindersSent++;
                g.LastReminderAtUtc = m.SentAtUtc;
                break;
            case MessageKind.ThankYou:
                g.ThankYouSentAtUtc = m.SentAtUtc;
                break;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
