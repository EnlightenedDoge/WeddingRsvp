using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.SiteAdmin;

public class NewCoupleModel(CoupleAdminService couples, UserManager<AppUser> users, Ui ui) : PageModel
{
    public string? Error { get; private set; }
    public NewCouple Input { get; private set; } = new("", "");
    public string StartsAtValue => Input.StartsAtLocal?.ToString("yyyy-MM-dd'T'HH:mm") ?? "";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(
        string? displayName, string? email, string? coupleNames, DateTime? startsAtLocal,
        string? venueName, string? venueAddress, string? smsSenderName)
    {
        Input = new NewCouple(displayName ?? "", email ?? "", coupleNames ?? "", startsAtLocal,
            venueName ?? "", venueAddress ?? "", smsSenderName ?? "");

        var actor = (await users.GetUserAsync(User))!;
        var r = await couples.CreateAsync(Input, actor);
        if (!r.Ok)
        {
            Error = r.Error == "email-taken"
                ? ui.T("כבר קיים חשבון עם האימייל הזה", "An account with this email already exists")
                : ui.T("בדקו את השם והאימייל", "Check the name and email");
            return Page();
        }

        // Shown once, on the detail page; it is also regenerable there.
        TempData["SetPasswordLink"] = r.SetPasswordLink;
        return Redirect($"/admin/site/couples/{r.Couple!.Id}");
    }
}
