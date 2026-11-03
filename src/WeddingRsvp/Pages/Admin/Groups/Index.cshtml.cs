using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Pages.Admin.Groups;

public class IndexModel(GroupLinkService groups, AuditLog audit, UserManager<AppUser> users, IOptions<AppOptions> app, Ui ui, ITenantContext tenant) : PageModel
{
    public List<GroupLinkRow> Rows { get; private set; } = [];
    public bool ShowForm { get; private set; }
    public int? EditId { get; private set; }
    public string? Error { get; private set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public int MaxGuests { get; set; } = 4;
    [BindProperty] public bool NoLimit { get; set; }
    [BindProperty] public GroupVerifyMode VerifyMode { get; set; } = GroupVerifyMode.Off;
    [BindProperty] public bool HasExpiry { get; set; }
    [BindProperty] public DateTime? ExpiresAt { get; set; }

    public string LinkUrl(GroupLink l) => MessageTemplates.GroupLink(app.Value.PublicBaseUrl, l.Code);

    public async Task<IActionResult> OnGetAsync(bool create, int? edit)
    {
        if (edit is { } id)
        {
            if (await groups.GetAsync(id) is not { } l) return NotFound();
            ShowForm = true; EditId = id;
            Name = l.Name; MaxGuests = l.NoGuestLimit ? 4 : l.MaxGuests; NoLimit = l.NoGuestLimit; VerifyMode = l.VerifyMode; HasExpiry = l.ExpiresAtLocal is not null; ExpiresAt = l.ExpiresAtLocal;
        }
        else if (create) ShowForm = true;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int? id)
    {
        var expires = HasExpiry ? ExpiresAt : null;
        if (NoLimit) MaxGuests = 0;
        var problem = GroupLinkService.Validate(Name, MaxGuests);
        if (problem is null && HasExpiry && expires is null) problem = "expiry";
        if (problem is not null)
        {
            Error = problem switch
            {
                "name" => ui.T("נא למלא שם לקבוצה (עד 100 תווים).", "Please enter a group name (up to 100 characters)."),
                "max" => ui.T($"מספר האורחים בתשובה חייב להיות בין 1 ל-{GroupLink.MaxGuestsLimit}, או ללא הגבלה.", $"Guests per answer must be between 1 and {GroupLink.MaxGuestsLimit}, or no limit."),
                _ => ui.T("נא לבחור תאריך תפוגה.", "Please choose an expiry date."),
            };
            ShowForm = true; EditId = id;
            await LoadAsync();
            return Page();
        }

        var user = await users.GetUserAsync(User);
        if (id is { } existing)
        {
            if (!await groups.UpdateAsync(existing, Name!, MaxGuests, expires, VerifyMode)) return NotFound();
            await audit.WriteAsync("grouplink.edited", user, tenant.CoupleId, Name!.Trim());
            TempData["Flash"] = ui.T("הקישור הקבוצתי עודכן", "Group link updated");
            return RedirectToPage();
        }
        var l = await groups.CreateAsync(Name!, MaxGuests, expires, VerifyMode);
        await audit.WriteAsync("grouplink.created", user, tenant.CoupleId, l.Name);
        TempData["Flash"] = ui.T("הקישור נוצר. אפשר להעתיק ולשלוח אותו.", "The link is created, ready to copy and send.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var l = await groups.GetAsync(id);
        if (l is null) return NotFound();
        var off = !l.IsOff;
        await groups.SetOffAsync(id, off);
        await audit.WriteAsync(off ? "grouplink.off" : "grouplink.on", await users.GetUserAsync(User), tenant.CoupleId, l.Name);
        TempData["Flash"] = off ? ui.T("הקישור הושבת", "The link is turned off") : ui.T("הקישור פעיל שוב", "The link is on again");
        return RedirectToPage();
    }

    private async Task LoadAsync() => Rows = await groups.ListAsync();
}
