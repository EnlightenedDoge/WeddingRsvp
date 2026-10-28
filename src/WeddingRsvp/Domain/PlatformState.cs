namespace WeddingRsvp.Domain;

/// <summary>Single row (Id = 1): platform-wide state shared by every couple. Not tenant-owned.</summary>
public class PlatformState
{
    public int Id { get; set; } = 1;
    /// <summary>The circuit breaker. While open nothing is sent for anyone; messages stay Queued.</summary>
    public bool ProviderOpen { get; set; }
    public string? Reason { get; set; }
    public DateTime? OpenedAtUtc { get; set; }
    public DateTime? LastProbeAtUtc { get; set; }
}
