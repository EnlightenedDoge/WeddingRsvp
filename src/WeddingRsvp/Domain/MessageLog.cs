namespace WeddingRsvp.Domain;

/// <summary>
/// Doubles as the outbox: rows start Queued, the dispatcher sends them and records the outcome.
/// </summary>
public class MessageLog
{
    public int Id { get; set; }
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
    public DateTime? DeliveredAtUtc { get; set; }

    public int Attempts { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
}
