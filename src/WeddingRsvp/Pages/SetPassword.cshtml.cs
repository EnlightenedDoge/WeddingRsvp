using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

/// <summary>Landing page of the one-time link the site admin passes on. Also used for "set a new password".</summary>
public class SetPasswordModel(UserManager<AppUser> users, AuditLog audit, Ui ui, TurnstileGate turnstile) : PageModel
{
    public bool Invalid { get; private set; }
    public string? Error { get; private set; }
    public string UserId { get; private set; } = "";
    public string Token { get; private set; } = "";

    public async Task OnGetAsync(string? u, string? t)
    {
        UserId = u ?? "";
        Token = t ?? "";
        Invalid = await Resolve(u, t) is null;
    }

    public async Task<IActionResult> OnPostAsync(string? u, string? t, string? password, string? confirm)
    {
        UserId = u ?? "";
        Token = t ?? "";
        if (await Resolve(u, t) is not { } user) { Invalid = true; return Page(); }

        if (await turnstile.CheckFormAsync("set-password") != TurnstileOutcome.Passed)
        {
            Error = ui.T("בדיקת האבטחה נכשלה. טענו את הדף מחדש ונסו שוב.", "The security check failed. Reload the page and try again.");
            return Page();
        }

        if (password != confirm)
        {
            Error = ui.T("הסיסמאות אינן זהות", "The passwords don't match");
            return Page();
        }

        var result = await users.ResetPasswordAsync(user, CoupleAdminService.DecodeToken(t!), password ?? "");
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken")) { Invalid = true; return Page(); }
            Error = ui.T("הסיסמה חלשה מדי (לפחות 10 תווים)", "That password is too weak (at least 10 characters)");
            return Page();
        }

        await users.ResetAccessFailedCountAsync(user);
        await audit.WriteAsync("password.set-by-link", user);
        TempData["Flash"] = ui.T("הסיסמה נשמרה. אפשר להיכנס.", "Password saved. You can sign in now.");
        return Redirect("/signin");
    }

    private async Task<AppUser?> Resolve(string? userId, string? token)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token)) return null;
        var user = await users.FindByIdAsync(userId);
        if (user is null) return null;
        try
        {
            var raw = CoupleAdminService.DecodeToken(token);
            return await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider,
                UserManager<AppUser>.ResetPasswordTokenPurpose, raw) ? user : null;
        }
        catch (FormatException) { return null; }
    }
}
