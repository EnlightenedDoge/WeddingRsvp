using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

/// <summary>Per-couple SMS credits as an append-only ledger. The remaining allocation is the sum of the deltas.</summary>
public sealed class AllocationService(AppDbContext db, IsraelClock clock, AuditLog audit)
{
    /// <summary>The current tenant's remaining credits (the query filter scopes it).</summary>
    public Task<int> BalanceAsync(CancellationToken ct = default) => db.SmsAllocations.SumAsync(e => e.Delta, ct);

    /// <summary>Queues a debit on the context without saving, so it commits atomically with the message's Sent mark.</summary>
    public void Debit(MessageLog m, int credits) => db.SmsAllocations.Add(new SmsAllocationEntry
    {
        Delta = -credits,
        Reason = $"message {m.Kind}",
        AtUtc = clock.UtcNow,
        MessageLogId = m.Id,
    });

    // --- Site admin side: no tenant is set, so couple-owned rows are addressed explicitly by id.

    public Task<int> BalanceForAsync(int coupleId, CancellationToken ct = default) =>
        db.SmsAllocations.IgnoreQueryFilters().Where(e => e.CoupleId == coupleId).SumAsync(e => e.Delta, ct);

    public Task<List<SmsAllocationEntry>> LedgerForAsync(int coupleId, int take = 50, CancellationToken ct = default) =>
        db.SmsAllocations.IgnoreQueryFilters().AsNoTracking().Where(e => e.CoupleId == coupleId)
            .OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);

    /// <summary>Grants (positive) or reduces (negative) a couple's allocation. Returns false if a reduction would go below zero.</summary>
    public async Task<bool> AdjustAsync(int coupleId, int delta, AppUser actor, CancellationToken ct = default)
    {
        if (delta == 0) return false;
        if (delta < 0 && await BalanceForAsync(coupleId, ct) + delta < 0) return false;
        db.SmsAllocations.Add(new SmsAllocationEntry
        {
            CoupleId = coupleId,
            Delta = delta,
            Reason = delta > 0 ? "granted" : "reduced",
            AtUtc = clock.UtcNow,
            ActorEmail = actor.Email,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("sms.allocation", actor, coupleId, delta.ToString("+#;-#"));
        return true;
    }
}
