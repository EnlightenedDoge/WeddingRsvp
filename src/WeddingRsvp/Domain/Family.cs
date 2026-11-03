namespace WeddingRsvp.Domain;

/// <summary>
/// A family: guests (each with their own phone, link and SMS) who share one answer. A response from any member is copied to all of
/// them, and the page greets them with the family name ("Hello Cohen family").
/// </summary>
public class Family : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>
    /// Optional identifier from the couple's own list (the import's "family identifier" column). Names can repeat (two Cohen families), so
    /// the identifier is what decides which family a row joins. Unique per couple when set.
    /// </summary>
    public string? ExternalId { get; set; }
    public const int ExternalIdMax = 64;
    /// <summary>The identifier shown to the couple: the one they gave (import column), or the generated F&lt;id&gt; (also what the CSV export uses).</summary>
    public string DisplayId => ExternalId ?? "F" + Id;
    public DateTime CreatedAtUtc { get; set; }
    public const int NameMax = 100;
}
