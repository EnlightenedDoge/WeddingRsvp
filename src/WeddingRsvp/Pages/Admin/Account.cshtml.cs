using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class AccountModel(
    UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountService account, AuditLog audit, Ui ui) : PageModel
{
    public string? Email { get; private set; }
    public string? PasswordError { get; private set; }
    public string? DeleteError { get; private set; }

    public async Task OnGetAsync() => Email = (await users.GetUserAsync(User))?.Email;

    public async Task<IActionResult> OnPostPasswordAsync(string? current, string? @new)
    {
        var user = (await users.GetUserAsync(User))!;
        Email = user.Email;
        var r = await users.ChangePasswordAsync(user, current ?? "", @new ?? "");
        if (!r.Succeeded)
        {
            await audit.WriteAsync("password.change-failed", user);
            PasswordError = r.Errors.Any(e => e.Code == "PasswordMismatch")
                ? ui.T("הסיסמה הנוכחית שגויה", "The current password is wrong")
                : ui.T("הסיסמה החדשה חלשה מדי (לפחות 10 תווים)", "The new password is too weak (at least 10 characters)");
            return Page();
        }
        await audit.WriteAsync("password.changed", user);
        await signIn.RefreshSignInAsync(user); // keep this session; other sessions are signed out
        TempData["Flash"] = ui.T("הסיסמה עודכנה", "Password updated");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAllAsync(string? deletePassword, string? confirm)
    {
        var user = (await users.GetUserAsync(User))!;
        Email = user.Email;
        if (confirm != "yes" || !await users.CheckPasswordAsync(user, deletePassword ?? ""))
        {
            await audit.WriteAsync("delete-all.refused", user);
            DeleteError = ui.T("הסיסמה שגויה", "The password is wrong");
            return Page();
        }
        await account.DeleteAllAsync(user);
        await signIn.SignOutAsync();
        return Redirect("/signin");
    }
}
