using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public sealed class AuditLog(AppDbContext db, IsraelClock clock, IHttpContextAccessor http)
{
    public async Task WriteAsync(string action, AppUser? actor = null, int? coupleId = null, string? detail = null)
    {
        var ctx = http.HttpContext;
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = clock.UtcNow,
            Action = action,
            ActorUserId = actor?.Id,
            ActorEmail = actor?.Email,
            CoupleId = coupleId ?? actor?.CoupleId,
            Detail = detail,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();
    }
}
