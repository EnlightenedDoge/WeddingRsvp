using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

/// <summary>
/// Platform-wide "provider unavailable" switch. An account-level refusal (no credit, bad credentials) opens it:
/// nothing is sent for anyone and queued messages simply wait. Persisted so a restart doesn't forget it.
/// </summary>
public sealed class ProviderCircuitBreaker(AppDbContext db, IsraelClock clock, AuditLog audit)
{
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(5);

    public async Task<PlatformState> GetAsync(CancellationToken ct = default)
    {
        var s = await db.PlatformStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is not null) return s;
        s = new PlatformState();
        db.PlatformStates.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<bool> IsOpenAsync(CancellationToken ct = default) => (await GetAsync(ct)).ProviderOpen;

    public async Task TripAsync(string reason, CancellationToken ct = default)
    {
        var s = await GetAsync(ct);
        if (s.ProviderOpen) return;
        s.ProviderOpen = true;
        s.Reason = reason;
        s.OpenedAtUtc = s.LastProbeAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("provider.breaker-tripped", detail: reason);
    }

    public async Task ResumeAsync(AppUser? actor = null, CancellationToken ct = default)
    {
        var s = await GetAsync(ct);
        if (!s.ProviderOpen) return;
        s.ProviderOpen = false;
        s.Reason = null;
        s.OpenedAtUtc = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("provider.breaker-resumed", actor, detail: actor is null ? "automatic" : "manual");
    }

    /// <summary>True when the breaker is open and the last probe is old enough; stamps the probe time.</summary>
    public async Task<bool> ProbeDueAsync(CancellationToken ct = default)
    {
        var s = await GetAsync(ct);
        if (!s.ProviderOpen) return false;
        var now = clock.UtcNow;
        if (s.LastProbeAtUtc is { } last && now - last < ProbeEvery) return false;
        s.LastProbeAtUtc = now;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
