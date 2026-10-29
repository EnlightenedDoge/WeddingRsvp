using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public enum DeliveryReportResult { Delivered, NotDelivered, Ignored, UnknownMessage }

/// <summary>
/// Applies a provider delivery report (SMS4FREE: form fields to, status, msgId; status 1 = delivered, 5 = not delivered).
/// msgId is our own MessageLog.Id, sent as usermsgid. Reports only move a message forward: a late report never downgrades
/// a message the guest has already opened.
/// </summary>
public sealed class DeliveryReports(AppDbContext db, IsraelClock clock, ILogger<DeliveryReports> log)
{
    public async Task<DeliveryReportResult> ApplyAsync(string? msgId, string? to, string? status, CancellationToken ct = default)
    {
        if (!int.TryParse(msgId, out var id))
        {
            log.LogWarning("Delivery report with unusable msgId '{MsgId}' (status {Status})", msgId, status);
            return DeliveryReportResult.UnknownMessage;
        }
        // Provider callbacks carry no couple: the message id decides which row this is.
        var m = await db.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        // The recipient must be present and match: the path secret alone is not enough to move a message.
        if (m is null || string.IsNullOrWhiteSpace(to) || !SamePhone(to, m.ToE164))
        {
            log.LogWarning("Delivery report for message {MsgId} matches nothing (status {Status})", msgId, status);
            return DeliveryReportResult.UnknownMessage;
        }

        switch (status?.Trim())
        {
            case "1":
                if (m.Status == MessageStatus.Sent)
                {
                    m.Status = MessageStatus.Delivered;
                    m.DeliveredAtUtc = clock.UtcNow;
                }
                await db.SaveChangesAsync(ct);
                return DeliveryReportResult.Delivered;
            case "5":
                if (m.Status == MessageStatus.Sent)
                {
                    m.Status = MessageStatus.Failed;
                    m.Error = "Not delivered (provider report)";
                }
                await db.SaveChangesAsync(ct);
                return DeliveryReportResult.NotDelivered;
            default:
                log.LogInformation("Ignoring delivery report status '{Status}' for message {MsgId}", status, msgId);
                return DeliveryReportResult.Ignored;
        }
    }

    /// <summary>Compares the Israeli national part (last 9 digits), so 05…, 9725… and +9725… all match.</summary>
    public static bool SamePhone(string a, string b)
    {
        static string Tail(string s)
        {
            var d = new string(s.Where(char.IsAsciiDigit).ToArray());
            return d.Length > 9 ? d[^9..] : d;
        }
        return Tail(a) == Tail(b);
    }
}
