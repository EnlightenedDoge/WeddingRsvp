namespace WeddingRsvp.Domain;

/// <summary>
/// Doubles as the outbox: rows start Queued, the dispatcher sends them and records the outcome.
/// </summary>
public class MessageLog : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public int? GuestId { get; set; }
    public Guest? Guest { get; set; }

    public MessageKind Kind { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Queued;

    public string ToE164 { get; set; } = "";
    public string Body { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Earliest time the dispatcher may send it (used for retry backoff).</summary>
    public DateTime NotBeforeUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    /// <summary>When the provider reported delivery, or the first link open if that came first (an open implies delivery).</summary>
    public DateTime? DeliveredAtUtc { get; set; }
    /// <summary>First time the guest opened the personal link after this message was sent.</summary>
    public DateTime? OpenedAtUtc { get; set; }

    public int Attempts { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
}
