using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WeddingRsvp.Pages;

/// <summary>
/// The public page behind every empty-bodied error status (see <c>UseStatusCodePagesWithReExecute</c>). A 404 is a link that does not
/// exist; anything else is our problem, not the guest's, so it says so and offers another go instead of "link not found".
/// </summary>
public class NotFoundModel : PageModel
{
    /// <summary>The status code that brought us here; 404 unless the error page was reached some other way.</summary>
    [BindProperty(SupportsGet = true, Name = "code")] public int Code { get; set; }

    public bool LinkMissing => Code is 404 or 0;
    public bool TooBusy => Code == 429;

    public void OnGet()
    {
        if (Code is < 400 or > 599) Code = 404;
        Response.StatusCode = Code;
    }
}
