using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using Microsoft.Extensions.Options;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Pages.Admin;

public class MessagesModel(
    AppDbContext db, CampaignService campaign, SettingsService settings, SendingWindow window, IsraelClock clock, Ui ui,
    AllocationService allocation, ProviderCircuitBreaker breaker,
    GroupLinkService groups, AuditLog audit, UserManager<AppUser> users, IOptions<AppOptions> app, ITenantContext tenant) : PageModel
{
    public List<GroupLinkRow> GroupRows { get; private set; } = [];
    public string GroupUrl(GroupLink l) => MessageTemplates.GroupLink(app.Value.PublicBaseUrl, l.Code);
    [BindProperty] public string? GroupName { get; set; }
    [BindProperty] public int GroupMax { get; set; } = 4;
    [BindProperty] public bool GroupNoLimit { get; set; }
    [BindProperty] public GroupVerifyMode GroupVerify { get; set; } = GroupVerifyMode.Off;
    [BindProperty] public bool GroupHasExpiry { get; set; }
    [BindProperty] public DateTime? GroupExpiresAt { get; set; }

    public EventSettings Event { get; private set; } = null!;
    public bool InSendingWindow { get; private set; }
    public int ReadyToInvite { get; private set; }
    /// <summary>The couple's remaining SMS credits.</summary>
    public int Remaining { get; private set; }
    /// <summary>Credits the pending invitations will use (0 while event details are missing), and the rough cost of one.</summary>
    public int InviteCredits { get; private set; }
    public int PerInviteCredits { get; private set; }
    /// <summary>Hebrew and English mode: every message holds both texts, so it is longer and can cost more.</summary>
    public bool BothMode { get; private set; }
    /// <summary>Platform-wide provider problem. Couples only ever see a neutral "delayed" note.</summary>
    public bool Delayed { get; private set; }
    /// <summary>Set when "send invitations" needs more credits than the couple has left and awaits a second confirmation.</summary>
    public (int Need, int Have)? Shortfall { get; private set; }
    /// <summary>Event details the invitation text uses that haven't been filled in (e.g. "{couple}"). Blocks sending.</summary>
    public IReadOnlyList<string> MissingForInvite { get; private set; } = [];
    public Dictionary<MessageStatus, int> Counts { get; private set; } = new();
    public List<MessageLog> Recent { get; private set; } = new();

    [BindProperty] public string? TestPhone { get; set; }
    [BindProperty] public MessageKind TestTemplate { get; set; } = MessageKind.Invite;

    public async Task OnGetAsync()
    {
        Event = await settings.GetAsync();
        GroupRows = await groups.ListAsync();
        Remaining = await allocation.BalanceAsync();
        var (inviteGuests, inviteCredits) = await campaign.InviteCostAsync();
        InviteCredits = inviteCredits;
        PerInviteCredits = inviteGuests > 0 ? (int)Math.Ceiling(inviteCredits / (double)inviteGuests) : 0;
        Delayed = await breaker.IsOpenAsync();
        if (TempData["InviteShortfall"] is string sf && sf.Split('|') is [var need, var have]
            && int.TryParse(need, out var n) && int.TryParse(have, out var h))
            Shortfall = (n, h);
        BothMode = Event.LanguageMode == LanguageMode.Both;
        InSendingWindow = window.IsAllowed(clock.LocalNow);
        MissingForInvite = MessageTemplates.MissingFields(Event, MessageKind.Invite);
        ReadyToInvite = await db.Guests.CountAsync(g => g.PhoneE164 != null && g.InviteSentAtUtc == null && g.GroupLinkId == null);
        Counts = await db.Messages.GroupBy(m => m.Status)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        Recent = await db.Messages.AsNoTracking().Include(m => m.Guest)
            .OrderByDescending(m => m.Id).Take(100).ToListAsync();
    }

    public async Task<IActionResult> OnPostQueueInvitesAsync(bool confirmShortfall = false)
    {
        var missing = await campaign.MissingFieldsAsync(MessageKind.Invite);
        if (missing.Count > 0)
        {
            TempData["Flash"] = ui.T("אי אפשר לשלוח הזמנות: חסרים פרטים באירוע (", "Can't send invitations: event details are missing (") + string.Join(" ", missing) + ")";
            return RedirectToPage();
        }
        var (_, need) = await campaign.InviteCostAsync();
        var have = await allocation.BalanceAsync();
        if (need > have && !confirmShortfall)
        {
            TempData["InviteShortfall"] = $"{need}|{have}";
            return RedirectToPage();
        }
        var n = await campaign.QueueInvitesAsync();
        TempData["Flash"] = n == 0 ? ui.T("אין הזמנות חדשות לשליחה", "No new invitations to send") : ui.T($"{n} הזמנות נכנסו לתור", $"{n} invitations queued");
        return RedirectToPage();
    }

    /// <summary>Creates a group link from the form on this page.</summary>
    public async Task<IActionResult> OnPostCreateGroupAsync()
    {
        var expires = GroupHasExpiry ? GroupExpiresAt : null;
        if (GroupNoLimit) GroupMax = 0;
        if (GroupLinkService.Validate(GroupName, GroupMax) is not null || (GroupHasExpiry && expires is null))
        {
            TempData["Flash"] = ui.T("לא נוצר קישור: נא למלא שם לקבוצה, מספר אורחים בין 1 ל-50 או ללא הגבלה, ותאריך אם סומן.", "Link not created: enter a group name, 1 to 50 guests or no limit, and a date if expiry is ticked.");
            return RedirectToPage();
        }
        var l = await groups.CreateAsync(GroupName!, GroupMax, expires, GroupVerify);
        await audit.WriteAsync("grouplink.created", await users.GetUserAsync(User), tenant.CoupleId, l.Name);
        TempData["Flash"] = ui.T("הקישור הקבוצתי נוצר. אפשר להעתיק ולשלוח אותו.", "The group link is created, ready to copy and send.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleGroupAsync(int id)
    {
        var l = await groups.GetAsync(id);
        if (l is null) return NotFound();
        await groups.SetOffAsync(id, !l.IsOff);
        await audit.WriteAsync(l.IsOff ? "grouplink.off" : "grouplink.on", await users.GetUserAsync(User), tenant.CoupleId, l.Name);
        return RedirectToPage();
    }

    [BindProperty] public int? ChildMinAge { get; set; }
    [BindProperty] public int? ChildMaxAge { get; set; }

    public async Task<IActionResult> OnPostChildAgeAsync()
    {
        if (ChildMinAge is < 0 or > EventSettings.MaxChildAge || ChildMaxAge is < 0 or > EventSettings.MaxChildAge
            || (ChildMinAge is { } a && ChildMaxAge is { } b && a > b))
        {
            TempData["Flash"] = ui.T($"טווח גילאים לא תקין: מספרים בין 0 ל-{EventSettings.MaxChildAge}, והראשון לא גדול מהשני.", $"Invalid age range: numbers from 0 to {EventSettings.MaxChildAge}, the first not above the second.");
            return RedirectToPage();
        }
        var s = await settings.GetAsync();
        s.ChildMinAge = ChildMinAge;
        s.ChildMaxAge = ChildMaxAge;
        await db.SaveChangesAsync();
        TempData["Flash"] = ui.T("טווח הגילאים נשמר", "The age range is saved");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTogglePauseAsync()
    {
        var s = await settings.GetAsync();
        s.SendingPaused = !s.SendingPaused;
        await db.SaveChangesAsync();
        TempData["Flash"] = s.SendingPaused ? ui.T("השליחה הושהתה", "Sending paused") : ui.T("השליחה חודשה", "Sending resumed");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequeueFailedAsync()
    {
        var n = await campaign.RequeueFailedAsync();
        TempData["Flash"] = ui.T($"{n} הודעות הוחזרו לתור", $"{n} messages re-queued");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        TempData["Flash"] = await campaign.QueueTestAsync(TestPhone ?? "", TestTemplate)
            ? ui.T("הודעת בדיקה נכנסה לתור", "Test message queued")
            : ui.T("מספר לא תקין", "Invalid number");
        return RedirectToPage();
    }
}
