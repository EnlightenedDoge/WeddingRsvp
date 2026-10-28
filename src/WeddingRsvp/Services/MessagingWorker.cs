using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Options;
using WeddingRsvp.Services.Sms;
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

        await ProbeProviderAsync(ct);

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

    /// <summary>While the breaker is open, ask the provider for its balance now and then; a positive answer closes it.</summary>
    private async Task ProbeProviderAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var breaker = scope.ServiceProvider.GetRequiredService<ProviderCircuitBreaker>();
            if (!await breaker.ProbeDueAsync(ct)) return;
            var balance = await scope.ServiceProvider.GetRequiredService<ISmsProvider>().GetBalanceAsync(ct);
            if (balance is > 0) await breaker.ResumeAsync(ct: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Provider probe failed");
        }
    }
}
