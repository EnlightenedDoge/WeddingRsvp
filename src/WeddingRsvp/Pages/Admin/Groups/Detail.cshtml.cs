using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Pages.Admin.Groups;

public class DetailModel(GroupLinkService groups, SettingsService settings, IOptions<AppOptions> app) : PageModel
{
    public GroupLink Link { get; private set; } = null!;
    public GroupLinkStatus Status { get; private set; }
    public List<GroupResponse> Responses { get; private set; } = [];
    public string Url { get; private set; } = "";
    public string MessageText { get; private set; } = "";
    public int Answers => Responses.Count;
    public int Guests => Responses.Sum(r => r.People);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (await groups.GetAsync(id) is not { } l) return NotFound();
        var s = await settings.GetAsync();
        Link = l;
        Status = groups.StatusOf(l, s);
        Responses = await groups.ResponsesAsync(id);
        Url = MessageTemplates.GroupLink(app.Value.PublicBaseUrl, l.Code);
        MessageText = MessageTemplates.GroupMessage(s, Url);
        return Page();
    }
}
