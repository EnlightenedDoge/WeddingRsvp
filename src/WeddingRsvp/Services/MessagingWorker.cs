using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Options;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Services;

/// <summary>Every tick: plan (queue what's due), then dispatch (send what's queued).</summary>
public sealed class MessagingWorker(
    IServiceScopeFactory scopes, IOptions<SendingOptions> options, ILogger<MessagingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.TickSeconds)));
        do
        {
            await TickAsync(ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One pass over every active couple. A failure for one couple never blocks the others.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        List<int> coupleIds;
        try
        {
            using var scope = scopes.CreateScope();
            coupleIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Couples.Where(c => !c.Suspended).Select(c => c.Id).ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Messaging tick failed to list couples");
            return;
        }

        foreach (var id in coupleIds)
        {
            try
            {
                using var scope = scopes.CreateScope(); // own scope = own DbContext + own tenant
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(id);
                await scope.ServiceProvider.GetRequiredService<CampaignService>().PlanAsync(ct);
                await scope.ServiceProvider.GetRequiredService<MessageDispatcher>().DispatchAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Messaging tick failed for couple {CoupleId}", id);
            }
        }
    }
}
