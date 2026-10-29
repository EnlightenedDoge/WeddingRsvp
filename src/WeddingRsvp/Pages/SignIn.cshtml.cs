using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using Microsoft.Extensions.Options;
using WeddingRsvp.Options;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public class SignInModel(
    SignInManager<AppUser> signIn, UserManager<AppUser> users, AppDbContext db, AuditLog audit, Ui ui,
    IOptions<AppOptions> app, TurnstileGate turnstile) : PageModel
{
    public string? Error { get; private set; }
    public string Email { get; private set; } = "";
    public string? ReturnUrl { get; private set; }

    // A valid hash to verify against on the unknown-user path, so that branch costs the same as a real password check.
    private static readonly string DummyPasswordHash = new PasswordHasher<AppUser>()
        .HashPassword(new AppUser(), "not-a-real-password-timing-equalizer");

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

        // Bot check first, before the email or password is looked at. A failed check never reaches the password, so it can't lock an account.
        if (await turnstile.CheckFormAsync("signin") != TurnstileOutcome.Passed)
        {
            await audit.WriteAsync("signin.failed", null, detail: "turnstile");
            Error = ui.T("בדיקת האבטחה נכשלה. טענו את הדף מחדש ונסו שוב.", "The security check failed. Reload the page and try again.");
            return Page();
        }

        var user = Email.Length > 0 ? await users.FindByEmailAsync(Email) : null;

        // One neutral message for every failure: wrong password, unknown email, suspended or deleted account.
        var generic = ui.T("אימייל או סיסמה שגויים", "Wrong email or password");

        // The site admin signs in only on the private admin port; on the public port that account looks like any wrong login.
        var siteAdminOnPublicPort = user?.Role == UserRole.SiteAdmin && HttpContext.Connection.LocalPort != app.Value.AdminPort;

        if (user is null || string.IsNullOrEmpty(password) || siteAdminOnPublicPort || !await CoupleIsActiveAsync(user))
        {
            // Do the same password-hash work a real sign-in would, so an unknown email can't be told apart from a wrong
            // password by response time (no account-enumeration oracle). The result is discarded.
            users.PasswordHasher.VerifyHashedPassword(user ?? new AppUser(), DummyPasswordHash, password ?? "");
            await audit.WriteAsync("signin.failed", null,
                detail: user is null ? "unknown-user" : siteAdminOnPublicPort ? "site-admin-on-public-port" : "blocked");
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
