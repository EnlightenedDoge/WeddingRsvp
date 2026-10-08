using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Auth;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class ExitCoupleModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AuditLog audit) : PageModel
{
    public IActionResult OnGet() => Redirect("/admin");

    public async Task<IActionResult> OnPostAsync()
    {
        if (!User.IsActingAsCouple()) return Redirect("/admin");
        var admin = (await users.GetUserAsync(User))!;
        var id = User.CoupleId();
        await signIn.SignInAsync(admin, false); // plain site-admin cookie again
        await audit.WriteAsync("couple.left", admin, id);
        return Redirect("/admin/site/couples");
    }
}
