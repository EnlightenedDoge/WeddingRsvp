using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class IndexModel(DashboardService dashboard, SettingsService settings) : PageModel
{
    public DashboardStats Stats { get; private set; } = null!;
    public EventSettings Event { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Stats = await dashboard.GetAsync();
        Event = await settings.GetAsync();
    }
}
