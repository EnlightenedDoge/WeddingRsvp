using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.SiteAdmin;

public class CoupleDetailModel(AppDbContext db, CoupleAdminService couples, UserManager<AppUser> users,
    SignInManager<AppUser> signIn, AuditLog audit) : PageModel
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

    /// <summary>
    /// Re-issues the site admin's cookie with the couple's id and an "acting" marker. The tenant filter then
    /// applies exactly as for the couple; nothing is loosened. Opening and leaving are audited.
    /// </summary>
    public async Task<IActionResult> OnPostOpenAsync(int id)
    {
        if (!await LoadAsync(id) || Couple.Suspended) return NotFound();
        var admin = (await users.GetUserAsync(User))!;
        await signIn.SignInWithClaimsAsync(admin, false, new[]
        {
            new System.Security.Claims.Claim(Auth.Roles.CoupleIdClaim, id.ToString()),
            new System.Security.Claims.Claim(Auth.Roles.ActingClaim, "1"),
        });
        await audit.WriteAsync("couple.opened-as", admin, id);
        return Redirect("/admin");
    }

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
