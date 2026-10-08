namespace WeddingRsvp.Tenancy;

/// <summary>
/// Which couple the current unit of work acts for. Scoped. Set from the signed-in user's claim, from a guest
/// token on the public pages, or per couple by the background worker. With no tenant set, tenant-owned
/// queries match nothing (safe by default).
/// </summary>
public interface ITenantContext
{
    int? CoupleId { get; }
    void Use(int coupleId);
}

public sealed class TenantContext : ITenantContext
{
    public int? CoupleId { get; private set; }
    public void Use(int coupleId) => CoupleId = coupleId;
}
