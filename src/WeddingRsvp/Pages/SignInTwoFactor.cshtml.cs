using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public class SignInTwoFactorModel(SignInManager<AppUser> signIn, AuditLog audit, Ui ui) : PageModel
{
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? Redirect("/signin") : Page();

    public async Task<IActionResult> OnPostAsync(string? code)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return Redirect("/signin");

        var clean = (code ?? "").Replace(" ", "").Replace("-", "");
        var r = await signIn.TwoFactorAuthenticatorSignInAsync(clean, isPersistent: false, rememberClient: false);
        if (r.Succeeded)
        {
            await audit.WriteAsync("signin.ok", user, detail: "2fa");
            return Redirect("/admin/site/couples");
        }
        if (r.IsLockedOut)
        {
            await audit.WriteAsync("signin.locked-out", user, detail: "2fa");
            return Redirect("/signin");
        }
        await audit.WriteAsync("signin.failed", user, detail: "bad-2fa-code");
        Error = ui.T("הקוד שגוי", "That code is wrong");
        return Page();
    }
}
