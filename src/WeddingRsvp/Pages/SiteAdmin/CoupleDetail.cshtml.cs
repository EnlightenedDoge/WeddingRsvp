using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.SiteAdmin;

public class CoupleDetailModel(AppDbContext db, CoupleAdminService couples, UserManager<AppUser> users) : PageModel
{
    public Couple Couple { get; private set; } = null!;
    public string? Email { get; private set; }
    public bool HasPassword { get; private set; }
    public string? Link { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Link = TempData["SetPasswordLink"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostNewLinkAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var owner = (await couples.PrimaryUserAsync(id))!;
        TempData["SetPasswordLink"] = await couples.IssueSetPasswordLinkAsync(owner, (await users.GetUserAsync(User))!);
        return Redirect($"/admin/site/couples/{id}");
    }

    public Task<IActionResult> OnPostSuspendAsync(int id) => SetSuspended(id, true);
    public Task<IActionResult> OnPostResumeAsync(int id) => SetSuspended(id, false);

    private async Task<IActionResult> SetSuspended(int id, bool suspended)
    {
        if (!await LoadAsync(id)) return NotFound();
        await couples.SetSuspendedAsync(id, suspended, (await users.GetUserAsync(User))!);
        return Redirect($"/admin/site/couples/{id}");
    }

    private async Task<bool> LoadAsync(int id)
    {
        var c = await db.Couples.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null);
        if (c is null) return false;
        Couple = c;
        var owner = await couples.PrimaryUserAsync(id);
        Email = owner?.Email;
        HasPassword = owner?.PasswordHash != null;
        return true;
    }
}
