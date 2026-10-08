using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public class SignInModel(
    SignInManager<AppUser> signIn, UserManager<AppUser> users, AppDbContext db, AuditLog audit, Ui ui) : PageModel
{
    public string? Error { get; private set; }
    public string Email { get; private set; } = "";
    public string? ReturnUrl { get; private set; }

    public IActionResult OnGet(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect(Home(User.IsInRole("SiteAdmin")));
        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? email, string? password, string? returnUrl)
    {
        Email = email?.Trim() ?? "";
        ReturnUrl = returnUrl;
        var user = Email.Length > 0 ? await users.FindByEmailAsync(Email) : null;

        // One neutral message for every failure: wrong password, unknown email, suspended or deleted account.
        var generic = ui.T("אימייל או סיסמה שגויים", "Wrong email or password");

        if (user is null || string.IsNullOrEmpty(password) || !await CoupleIsActiveAsync(user))
        {
            await audit.WriteAsync("signin.failed", null, detail: user is null ? "unknown-user" : "blocked");
            Error = generic;
            return Page();
        }

        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await audit.WriteAsync("signin.ok", user);
            return LocalRedirect(SafeReturn(returnUrl) ?? Home(user.Role == UserRole.SiteAdmin));
        }
        if (result.RequiresTwoFactor) return Redirect("/signin/2fa");
        if (result.IsLockedOut)
        {
            await audit.WriteAsync("signin.locked-out", user);
            Error = ui.T("יותר מדי ניסיונות. נסו שוב בעוד 15 דקות.", "Too many attempts. Try again in 15 minutes.");
            return Page();
        }

        await audit.WriteAsync("signin.failed", user, detail: "bad-password");
        Error = generic;
        return Page();
    }

    private async Task<bool> CoupleIsActiveAsync(AppUser u) =>
        u.CoupleId is not { } cid
        || await db.Couples.AnyAsync(c => c.Id == cid && !c.Suspended && c.DeletedAtUtc == null);

    private static string Home(bool siteAdmin) => siteAdmin ? "/admin/site/couples" : "/admin";

    private static string? SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : null;
}
