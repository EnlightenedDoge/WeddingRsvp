namespace WeddingRsvp.Domain;

/// <summary>
/// Append-only ledger of a couple's SMS credits: positive = granted by the site admin, negative = reduced or spent.
/// A couple's remaining allocation is the sum of Delta.
/// </summary>
public class SmsAllocationEntry : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public int Delta { get; set; }
    public string Reason { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string? ActorEmail { get; set; }
    public int? MessageLogId { get; set; }
}
