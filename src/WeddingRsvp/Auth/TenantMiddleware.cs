using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Auth;

/// <summary>Binds the request to the signed-in couple. Runs after authentication.</summary>
public sealed class TenantMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx, ITenantContext tenant)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.CoupleId() is { } id)
            tenant.Use(id);
        return next(ctx);
    }
}
