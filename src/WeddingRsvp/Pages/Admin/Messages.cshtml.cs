using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class MessagesModel(
    AppDbContext db, CampaignService campaign, SettingsService settings, SendingWindow window, IsraelClock clock) : PageModel
{
    public EventSettings Event { get; private set; } = null!;
    public bool InSendingWindow { get; private set; }
    public int ReadyToInvite { get; private set; }
    /// <summary>Event details the invitation text uses that haven't been filled in (e.g. "{couple}"). Blocks sending.</summary>
    public IReadOnlyList<string> MissingForInvite { get; private set; } = [];
    public Dictionary<MessageStatus, int> Counts { get; private set; } = new();
    public List<MessageLog> Recent { get; private set; } = new();

    [BindProperty] public string? TestPhone { get; set; }
    [BindProperty] public MessageKind TestTemplate { get; set; } = MessageKind.Invite;

    public async Task OnGetAsync()
    {
        Event = await settings.GetAsync();
        InSendingWindow = window.IsAllowed(clock.LocalNow);
        MissingForInvite = MessageTemplates.MissingFields(Event, MessageKind.Invite);
        ReadyToInvite = await db.Guests.CountAsync(g => g.PhoneE164 != null && g.InviteSentAtUtc == null);
        Counts = await db.Messages.GroupBy(m => m.Status)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        Recent = await db.Messages.AsNoTracking().Include(m => m.Guest)
            .OrderByDescending(m => m.Id).Take(100).ToListAsync();
    }

    public async Task<IActionResult> OnPostQueueInvitesAsync()
    {
        var missing = await campaign.MissingFieldsAsync(MessageKind.Invite);
        if (missing.Count > 0)
        {
            TempData["Flash"] = "אי אפשר לשלוח הזמנות: חסרים פרטים באירוע (" + string.Join(" ", missing) + ")";
            return RedirectToPage();
        }
        var n = await campaign.QueueInvitesAsync();
        TempData["Flash"] = n == 0 ? "אין הזמנות חדשות לשליחה" : $"{n} הזמנות נכנסו לתור";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTogglePauseAsync()
    {
        var s = await settings.GetAsync();
        s.SendingPaused = !s.SendingPaused;
        await db.SaveChangesAsync();
        TempData["Flash"] = s.SendingPaused ? "השליחה הושהתה" : "השליחה חודשה";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequeueFailedAsync()
    {
        var n = await campaign.RequeueFailedAsync();
        TempData["Flash"] = $"{n} הודעות הוחזרו לתור";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        TempData["Flash"] = await campaign.QueueTestAsync(TestPhone ?? "", TestTemplate)
            ? "הודעת בדיקה נכנסה לתור"
            : "מספר לא תקין";
        return RedirectToPage();
    }
}
