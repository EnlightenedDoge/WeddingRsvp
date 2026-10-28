using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;

namespace WeddingRsvp.Pages.SiteAdmin;

public class ProviderModel(ProviderCircuitBreaker breaker, ISmsProvider sms, UserManager<AppUser> users) : PageModel
{
    public string Name => sms.Name;
    public PlatformState State { get; private set; } = null!;
    public int? Balance { get; private set; }

    public async Task OnGetAsync()
    {
        State = await breaker.GetAsync();
        Balance = await sms.GetBalanceAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostResumeAsync()
    {
        await breaker.ResumeAsync(await users.GetUserAsync(User));
        return RedirectToPage();
    }
}
