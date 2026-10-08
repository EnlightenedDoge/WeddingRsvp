namespace WeddingRsvp.Domain;

/// <summary>The tenant. Every guest, message and settings row belongs to exactly one couple.</summary>
public class Couple
{
    public int Id { get; set; }

    /// <summary>Label the site admin sees in lists (e.g. "Dana &amp; Avi"). Not used in guest messages.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>A suspended couple can't sign in and nothing is sent for them.</summary>
    public bool Suspended { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Set by "delete all information": all data and users are gone, this row stays as a tombstone
    /// (id + timestamps only) so the audit log still points at something.
    /// </summary>
    public DateTime? DeletedAtUtc { get; set; }
}

/// <summary>Implemented by every row that belongs to a couple; the DbContext filters and stamps it.</summary>
public interface ITenantOwned
{
    int CoupleId { get; set; }
}
