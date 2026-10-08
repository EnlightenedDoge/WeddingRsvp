using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class IndexModel(DashboardService dashboard) : PageModel
{
    public DashboardStats Stats { get; private set; } = null!;

    public async Task OnGetAsync() => Stats = await dashboard.GetAsync();
}
