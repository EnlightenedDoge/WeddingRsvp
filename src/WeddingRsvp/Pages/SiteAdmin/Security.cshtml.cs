using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.SiteAdmin;

public class SecurityModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AuditLog audit, Ui ui) : PageModel
{
    public bool Enabled { get; private set; }
    public string SharedKey { get; private set; } = "";
    public string Uri { get; private set; } = "";
    public string? Error { get; private set; }
    public string? PasswordError { get; private set; }

    public async Task OnGetAsync() => await LoadAsync(await users.GetUserAsync(User) ?? throw new InvalidOperationException());

    public async Task<IActionResult> OnPostEnableAsync(string? code)
    {
        var user = (await users.GetUserAsync(User))!;
        var clean = (code ?? "").Replace(" ", "").Replace("-", "");
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, clean))
        {
            Error = ui.T("הקוד שגוי", "That code is wrong");
            await LoadAsync(user);
            return Page();
        }
        await users.SetTwoFactorEnabledAsync(user, true);
        await audit.WriteAsync("2fa.enabled", user);
        TempData["Flash"] = ui.T("האימות הדו-שלבי הופעל", "Two-factor authentication enabled");
        return RedirectToPage();
    }

    /// <summary>
    /// The site admin changes their own password. Wrong attempts count toward lockout (like sign-in), the new password follows the
    /// same policy as everyone's (at least 10 characters), and this session stays signed in while other sessions are signed out.
    /// </summary>
    public async Task<IActionResult> OnPostPasswordAsync(string? current, string? @new, string? confirm)
    {
        var user = (await users.GetUserAsync(User))!;
        string? problem = null;
        if (string.IsNullOrEmpty(@new) || @new != confirm)
            problem = ui.T("הסיסמה החדשה והאימות אינם זהים", "The new password and its confirmation don't match");
        else if (@new == current)
            problem = ui.T("בחרו סיסמה שונה מהנוכחית", "Choose a password different from the current one");
        else if (!(await signIn.CheckPasswordSignInAsync(user, current ?? "", lockoutOnFailure: true)).Succeeded)
        {
            await audit.WriteAsync("password.change-failed", user);
            problem = ui.T("הסיסמה הנוכחית שגויה", "The current password is wrong");
        }
        else if (!(await users.ChangePasswordAsync(user, current!, @new)).Succeeded)
            problem = ui.T("הסיסמה החדשה חלשה מדי (לפחות 10 תווים)", "The new password is too weak (at least 10 characters)");

        if (problem is not null)
        {
            PasswordError = problem;
            await LoadAsync(user);
            return Page();
        }
        await audit.WriteAsync("password.changed", user);
        await signIn.RefreshSignInAsync(user);
        TempData["Flash"] = ui.T("הסיסמה עודכנה", "Password updated");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync(string? password)
    {
        var user = (await users.GetUserAsync(User))!;
        if (!await users.CheckPasswordAsync(user, password ?? ""))
        {
            TempData["Flash"] = ui.T("הסיסמה שגויה", "The password is wrong");
            return RedirectToPage();
        }
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await audit.WriteAsync("2fa.disabled", user);
        return RedirectToPage();
    }

    private async Task LoadAsync(AppUser user)
    {
        Enabled = await users.GetTwoFactorEnabledAsync(user);
        if (Enabled) return;
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        SharedKey = string.Join(' ', Enumerable.Range(0, (key!.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
        Uri = $"otpauth://totp/WeddingRsvp:{System.Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=WeddingRsvp&digits=6";
    }
}
