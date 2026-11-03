using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages;

public enum GroupStage { Phone, Code, Form, Counted, OnList, Closed, Saved }

public sealed class PersonInput
{
    public string? First { get; set; }
    public string? Last { get; set; }
    public string? Phone { get; set; }
}

/// <summary>
/// The page behind a group link (/g/{code}): phone number first, then the details of the person answering and of everyone they bring.
/// Anyone with the link can open it; the phone number is the identity, optionally proven by an SMS code (see <see cref="GroupVerifyMode"/>).
/// </summary>
/// <remarks>
/// Anonymous and open by design: there is no session to forge, and spam is held by Turnstile and the rate limit. Without the antiforgery check a guest
/// whose browser holds an older token or another session (a couple signed in on the same host) never meets a bare 400.
/// </remarks>
[Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
public class GroupRsvpModel(
    GroupLinkService groups, PhoneVerificationService verify, SettingsService settings, TurnstileGate turnstile, ILogger<GroupRsvpModel> log,
    Microsoft.AspNetCore.Authorization.IAuthorizationService authz) : PageModel
{
    public const string PhoneAction = "group-phone";
    public const string SubmitAction = "group-rsvp";
    public const string CodeAction = "group-code";

    public GroupLink Link { get; private set; } = null!;
    public EventSettings Event { get; private set; } = null!;
    public GroupStage Stage { get; private set; } = GroupStage.Phone;
    /// <summary>The couple's own preview (/admin/preview/group): the real page with a made-up link; answers are checked but never stored.</summary>
    public bool IsPreview => Request.Path.StartsWithSegments("/admin/preview");
    public string BasePath => IsPreview ? "/admin/preview/group" : "/g/" + Link.Code;
    /// <summary>Where the invitation image, background and calendar file are served from.</summary>
    public string MediaBase => IsPreview ? "/admin/preview" : "/g/" + Link.Code;

    // Look (same as the personal page).
    public string? ImageUrl { get; private set; }
    public string GoogleCalendarUrl { get; private set; } = "";
    public string? WazeUrl { get; private set; }
    public string? GoogleMapsUrl { get; private set; }
    public string Bg { get; private set; } = HexColor.DefaultBackground;
    public string Accent { get; private set; } = HexColor.DefaultAccent;
    public bool Dark { get; private set; }
    public string? BackgroundUrl { get; private set; }
    public string Title { get; private set; } = "";
    public string CoupleDisplay { get; private set; } = "";
    public string VenueDisplay { get; private set; } = "";
    public bool IsHe { get; private set; } = true;
    public bool ShowToggle { get; private set; }
    public string T(string he, string en) => IsHe ? he : en;
    public CultureInfo Culture => IsHe ? new CultureInfo("he-IL") : new CultureInfo("en-US");
    [BindProperty(SupportsGet = true)] public string? Lang { get; set; }
    /// <summary>Preview only: which verification mode to try (off | edit | always).</summary>
    [BindProperty(SupportsGet = true)] public string? Verify { get; set; }
    public GroupVerifyMode PreviewMode => Verify switch { "always" => GroupVerifyMode.Always, "edit" => GroupVerifyMode.OnEdit, _ => GroupVerifyMode.Off };

    // The form.
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public bool Takeover { get; set; }
    [BindProperty] public string? Otp { get; set; }
    /// <summary>Signed proof that the phone was verified; carried by the form and checked again when the answer is saved.</summary>
    [BindProperty] public string? Proof { get; set; }
    [BindProperty] public string? First { get; set; }
    [BindProperty] public string? Last { get; set; }
    [BindProperty] public bool? Attending { get; set; }
    [BindProperty] public int Adults { get; set; } = 1;
    [BindProperty] public int Children { get; set; }
    [BindProperty] public List<PersonInput> AdultGuests { get; set; } = [];
    [BindProperty] public List<PersonInput> ChildGuests { get; set; } = [];

    public string? OwnerName { get; private set; }
    public bool Returning { get; private set; }
    public string? PhoneError { get; private set; }
    public string? CodeError { get; private set; }
    /// <summary>Seconds before another code can be requested (drives the resend countdown).</summary>
    public int WaitSeconds { get; private set; }
    public string? FormError { get; private set; }
    public Dictionary<string, string> FieldErrors { get; } = new();
    public List<string> SavedNames { get; private set; } = [];

    public bool HasError(string field) => FieldErrors.ContainsKey(field);

    public async Task<IActionResult> OnGetAsync(string code)
    {
        var (link, denied) = await ResolveAsync(code);
        if (denied is not null) return denied;
        if (link is null) return NotFound();
        await LoadAsync(link);
        if (!IsPreview && await groups.IsClosedAsync(link)) Stage = GroupStage.Closed;
        return Page();
    }

    /// <summary>Step 1: the phone number. Decides what to show next.</summary>
    public async Task<IActionResult> OnPostPhoneAsync(string code)
    {
        var (link, denied) = await ResolveAsync(code);
        if (denied is not null) return denied;
        if (link is null) return NotFound();
        await LoadAsync(link);
        if (!IsPreview && await groups.IsClosedAsync(link)) { Stage = GroupStage.Closed; return Page(); }
        if (!IsPreview && await turnstile.CheckFormAsync(PhoneAction) != TurnstileOutcome.Passed)
        {
            PhoneError = T("בדיקת האבטחה נכשלה. טענו את הדף מחדש ונסו שוב.", "The security check failed. Reload the page and try again.");
            return Page();
        }
        return await ShowLookupAsync(link, takeover: false);
    }

    /// <summary>"I want to answer for myself": the person someone else added opens their own answer.</summary>
    public async Task<IActionResult> OnPostTakeoverAsync(string code)
    {
        var (link, denied) = await ResolveAsync(code);
        if (denied is not null) return denied;
        if (link is null) return NotFound();
        await LoadAsync(link);
        if (!IsPreview && await groups.IsClosedAsync(link)) { Stage = GroupStage.Closed; return Page(); }
        if (!IsPreview && await turnstile.CheckFormAsync(PhoneAction) != TurnstileOutcome.Passed)
        {
            PhoneError = T("בדיקת האבטחה נכשלה. טענו את הדף מחדש ונסו שוב.", "The security check failed. Reload the page and try again.");
            return Page();
        }
        return await ShowLookupAsync(link, takeover: true);
    }

    private GroupVerifyMode Mode => Link.VerifyMode;

    /// <summary>Whether this visitor must prove the phone with an SMS code before the answer is shown or saved.</summary>
    private bool NeedsCode(GroupLookupKind kind, bool takeover) => Mode switch
    {
        GroupVerifyMode.Always => kind is GroupLookupKind.New or GroupLookupKind.Returning or GroupLookupKind.CountedByOther,
        GroupVerifyMode.OnEdit => kind == GroupLookupKind.Returning || (kind == GroupLookupKind.CountedByOther && takeover),
        _ => false,
    };

    private async Task<IActionResult> ShowLookupAsync(GroupLink link, bool takeover)
    {
        var look = await groups.LookupAsync(Phone);
        switch (look.Kind)
        {
            case GroupLookupKind.Invalid:
                PhoneError = T("מספר הטלפון לא תקין. לדוגמה: 050-123-4567", "This phone number is not valid. Example: 050-123-4567");
                return Page();
            case GroupLookupKind.OnList:
                Stage = GroupStage.OnList;
                return Page();
            case GroupLookupKind.CountedByOther when !takeover:
                Stage = GroupStage.Counted;
                OwnerName = Mode == GroupVerifyMode.Off ? look.OwnerName : null; // with verification on, the name is not shown to an unverified visitor
                return Page();
        }
        if (NeedsCode(look.Kind, takeover)) return await StartCodeAsync(look.E164!, takeover);
        return await ShowFormAsync(look, takeover);
    }

    /// <summary>Sends a code (or just shows the code step if one went out a moment ago).</summary>
    private async Task<IActionResult> StartCodeAsync(string e164, bool takeover)
    {
        Stage = GroupStage.Code;
        Takeover = takeover;
        if (IsPreview) { WaitSeconds = 30; return Page(); } // the couple's preview sends nothing and accepts any 6 digits
        var sent = await verify.SendAsync(e164, IsHe);
        switch (sent.Kind)
        {
            case VerifySendKind.Sent or VerifySendKind.Cooldown:
                WaitSeconds = sent.WaitSeconds;
                return Page();
            case VerifySendKind.TooMany:
                Stage = GroupStage.Phone;
                PhoneError = T("התבקשו יותר מדי קודים למספר הזה. נסו שוב בעוד שעה.", "Too many codes were requested for this number. Try again in an hour.");
                return Page();
            default:
                Stage = GroupStage.Phone;
                PhoneError = T("האימות לא זמין כרגע. אפשר לפנות אל הזוג.", "Verification is not available right now. Please contact the couple.");
                return Page();
        }
    }

    /// <summary>Step 1b (when verification is on): the SMS code.</summary>
    public async Task<IActionResult> OnPostCodeAsync(string code)
    {
        var (link, denied) = await ResolveAsync(code);
        if (denied is not null) return denied;
        if (link is null) return NotFound();
        await LoadAsync(link);
        if (!IsPreview && await groups.IsClosedAsync(link)) { Stage = GroupStage.Closed; return Page(); }
        var look = await groups.LookupAsync(Phone);
        if (look.Kind is GroupLookupKind.Invalid or GroupLookupKind.OnList) return await ShowLookupAsync(link, Takeover);

        Stage = GroupStage.Code;
        if (IsPreview)
        {
            var digits = new string((Otp ?? "").Where(char.IsAsciiDigit).ToArray());
            if (digits.Length == PhoneVerificationService.CodeLength) { Proof = "preview"; return await ShowFormAsync(look, Takeover); }
            CodeError = T("הקוד שגוי. בתצוגה המקדימה כל קוד בן 6 ספרות מתקבל.", "That code is incorrect. In the preview any 6 digits are accepted.");
            WaitSeconds = 30;
            return Page();
        }
        var check = await verify.CheckAsync(look.E164!, Otp);
        if (check.Kind == VerifyCheckKind.Ok)
        {
            Proof = check.Proof;
            return await ShowFormAsync(look, Takeover);
        }
        CodeError = check.Kind switch
        {
            VerifyCheckKind.Expired => T("הקוד פג תוקף. בקשו קוד חדש.", "That code has expired. Request a new one."),
            VerifyCheckKind.Locked => T("יותר מדי ניסיונות שגויים. בקשו קוד חדש.", "Too many wrong attempts. Request a new code."),
            _ => T("הקוד שגוי. בדקו את ההודעה ונסו שוב.", "That code is incorrect. Check the message and try again."),
        };
        WaitSeconds = await verify.WaitSecondsAsync(look.E164!);
        return Page();
    }

    /// <summary>"Resend code": another SMS, which costs the couple a credit, so it is bot-checked and limited.</summary>
    public async Task<IActionResult> OnPostResendAsync(string code)
    {
        var (link, denied) = await ResolveAsync(code);
        if (denied is not null) return denied;
        if (link is null) return NotFound();
        await LoadAsync(link);
        if (!IsPreview && await groups.IsClosedAsync(link)) { Stage = GroupStage.Closed; return Page(); }
        if (!IsPreview && await turnstile.CheckFormAsync(CodeAction) != TurnstileOutcome.Passed)
        {
            Stage = GroupStage.Code;
            CodeError = T("בדיקת האבטחה נכשלה. טענו את הדף מחדש ונסו שוב.", "The security check failed. Reload the page and try again.");
            return Page();
        }
        var look = await groups.LookupAsync(Phone);
        if (look.Kind is GroupLookupKind.Invalid or GroupLookupKind.OnList) return await ShowLookupAsync(link, Takeover);
        return await StartCodeAsync(look.E164!, Takeover);
    }

    /// <summary>Shows the answer form, filled in for a returning visitor or for someone taking over.</summary>
    private async Task<IActionResult> ShowFormAsync(GroupLookup look, bool takeover)
    {
        Stage = GroupStage.Form;
        Adults = 1; Children = 0; Attending = null; AdultGuests = []; ChildGuests = [];
        if (look.Existing is { } ex)
        {
            (First, Last) = GroupLinkService.SplitName(ex.Name);
            if (look.Kind == GroupLookupKind.CountedByOther) { Takeover = true; OwnerName = Mode == GroupVerifyMode.Off ? look.OwnerName : null; }
            else
            {
                Returning = true;
                Attending = ex.Status == RsvpStatus.Attending ? true : ex.Status == RsvpStatus.Declined ? false : null;
                if (ex.Status == RsvpStatus.Attending)
                {
                    var added = await groups.AddedByAsync(ex.Id);
                    foreach (var a in added)
                    {
                        var (f, l) = GroupLinkService.SplitName(a.Name);
                        var person = new PersonInput { First = f, Last = l, Phone = a.PhoneRaw };
                        (a.IsChild ? ChildGuests : AdultGuests).Add(person);
                    }
                    Adults = 1 + AdultGuests.Count; Children = ChildGuests.Count;
                }
            }
        }
        return Page();
    }

    /// <summary>Step 2: save the answer.</summary>
    public async Task<IActionResult> OnPostSubmitAsync(string code)
    {
        var (link, denied) = await ResolveAsync(code);
        if (denied is not null) return denied;
        if (link is null) return NotFound();
        await LoadAsync(link);
        Stage = GroupStage.Form;
        var look = await groups.LookupAsync(Phone);
        Returning = look.Kind == GroupLookupKind.Returning;
        if (!IsPreview && await groups.IsClosedAsync(link)) { Stage = GroupStage.Closed; return Page(); }
        // The phone must have been proven when the couple asks for it. The proof comes from the code step and is signed, so it can't be forged.
        if (!IsPreview && look.Kind != GroupLookupKind.Invalid && NeedsCode(look.Kind, Takeover) && !verify.ProofIsValid(Proof, look.E164!))
        {
            Stage = GroupStage.Phone;
            PhoneError = T("האימות פג תוקף. הזינו שוב את מספר הטלפון.", "The verification expired. Please enter your number again.");
            return Page();
        }
        if (!IsPreview && await turnstile.CheckFormAsync(SubmitAction) != TurnstileOutcome.Passed)
        {
            FormError = T("בדיקת האבטחה נכשלה. טענו את הדף מחדש ונסו שוב.", "The security check failed. Reload the page and try again.");
            Pad();
            return Page();
        }
        if (Attending is null)
        {
            FormError = T("נא לבחור אם מגיעים כדי להמשיך.", "Please choose whether you are attending to continue.");
            Pad();
            return Page();
        }

        // Cards past the chosen counts are ignored; missing ones count as empty (and fail the name check).
        Pad();
        var attending = Attending.Value;
        if (attending && (Adults < 1 || Children < 0 || (long)Adults + Children > link.EffectiveMax))
        {
            FormError = T($"אפשר לרשום עד {link.EffectiveMax} אורחים בכל תשובה, ולפחות מבוגר אחד.",
                $"Up to {link.EffectiveMax} guests per answer, and at least 1 adult.");
            return Page();
        }
        var others = AdultGuests.Take(Math.Max(0, Adults - 1)).Select(p => new GroupPerson(p.First ?? "", p.Last ?? "", p.Phone, false))
            .Concat(ChildGuests.Take(Children).Select(p => new GroupPerson(p.First ?? "", p.Last ?? "", p.Phone, true))).ToList();

        var result = IsPreview
            ? await groups.SubmitAsync(link, First, Last, Phone, attending, others, Takeover, dryRun: true)
            : await groups.SubmitAsync(code, First, Last, Phone, attending, others, Takeover);
        switch (result.Kind)
        {
            case GroupSubmitKind.Saved:
                if (!IsPreview) log.LogInformation("Group link {LinkId}: answer saved from {Ip}", link.Id, HttpContext.Connection.RemoteIpAddress);
                Stage = GroupStage.Saved;
                SavedNames = result.Names.ToList();
                return Page();
            case GroupSubmitKind.Closed:
                Stage = GroupStage.Closed;
                return Page();
            case GroupSubmitKind.OnList:
                Stage = GroupStage.OnList;
                return Page();
            case GroupSubmitKind.CountedByOther:
                Stage = GroupStage.Counted;
                OwnerName = Mode == GroupVerifyMode.Off ? (await groups.LookupAsync(Phone)).OwnerName : null;
                return Page();
            default:
                foreach (var e in result.Errors) FieldErrors.TryAdd(e.Field, e.Code);
                FormError = result.Errors.Any(e => e.Field == "count")
                    ? T($"אפשר לרשום עד {link.EffectiveMax} אורחים בכל תשובה.", $"Up to {link.EffectiveMax} guests per answer.")
                    : T($"יש לתקן {FieldErrors.Count} שדות כדי לשלוח.", FieldErrors.Count == 1 ? "Please fix 1 field to send." : $"Please fix {FieldErrors.Count} fields to send.");
                return Page();
        }
    }

    /// <summary>Make the lists exactly as long as the counts say, so every card the page shows has a slot.</summary>
    private void Pad()
    {
        Adults = Math.Clamp(Adults, 1, Math.Max(1, Link.EffectiveMax));
        Children = Math.Clamp(Children, 0, Math.Max(0, Link.EffectiveMax - Adults));
        while (AdultGuests.Count < Adults - 1) AdultGuests.Add(new());
        while (ChildGuests.Count < Children) ChildGuests.Add(new());
    }

    /// <summary>The link for this request: the real one by code, or a made-up one for the couple's preview (which needs their session).</summary>
    private async Task<(GroupLink? Link, IActionResult? Denied)> ResolveAsync(string? code)
    {
        if (!IsPreview) return (await groups.FindAsync(code ?? ""), null);
        if (!(await authz.AuthorizeAsync(User, "Couple")).Succeeded) return (null, Challenge());
        var he = (await settings.GetAsync()).LanguageMode != LanguageMode.EnglishOnly;
        return (new GroupLink { Name = he ? "החברים מהעבודה" : "Friends from work", Code = "PREVIEW", MaxGuests = 4, VerifyMode = PreviewMode }, null);
    }

    private async Task LoadAsync(GroupLink link)
    {
        Link = link;
        Event = await settings.GetAsync();
        var look = GuestPageLook.Build(Event, Lang, MediaBase);
        IsHe = look.IsHe; ShowToggle = look.ShowToggle; ImageUrl = look.ImageUrl;
        GoogleCalendarUrl = look.GoogleCalendarUrl; WazeUrl = look.WazeUrl; GoogleMapsUrl = look.GoogleMapsUrl;
        Bg = look.Bg; Accent = look.Accent; BackgroundUrl = look.BackgroundUrl; Dark = look.Dark;
        CoupleDisplay = look.CoupleDisplay; Title = look.Title; VenueDisplay = look.VenueDisplay;
    }
}
