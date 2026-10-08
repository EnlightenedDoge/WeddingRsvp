namespace WeddingRsvp.Domain;

/// <summary>Who did what. Never contains guest names, phone numbers or message text.</summary>
public class AuditEntry
{
    public int Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Action { get; set; } = "";
    public string? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    /// <summary>The couple the action concerned, if any (no foreign key: entries outlive deleted couples).</summary>
    public int? CoupleId { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
}
