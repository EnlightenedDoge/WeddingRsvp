using Microsoft.Extensions.Options;
using WeddingRsvp.Options;

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
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CampaignService>().PlanAsync(ct);
                await scope.ServiceProvider.GetRequiredService<MessageDispatcher>().DispatchAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Messaging tick failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
