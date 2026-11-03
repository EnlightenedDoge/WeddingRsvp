namespace WeddingRsvp.Domain;

/// <summary>
/// One invitation (a household / couple / single guest), reachable on one phone number.
/// InvitedCount is how many people this invitation covers. 0 means "no guest limit": the invitation may bring any number of
/// people (the RSVP page then accepts 1 to <see cref="UnlimitedCap"/>).
/// All *Utc properties are stored and read as UTC (see AppDbContext).
/// </summary>
public class Guest : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>True only for the made-up guest behind a couple's demo link; never stored.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public bool IsDemo { get; init; }

    /// <summary>Phone as it appeared in the client's list.</summary>
    public string PhoneRaw { get; set; } = "";

    /// <summary>Normalized E.164 number, or null when the raw value couldn't be normalized.</summary>
    public string? PhoneE164 { get; set; }
    public string? PhoneError { get; set; }

    public int InvitedCount { get; set; } = 1;

    /// <summary>Most people an RSVP can name when the invitation has no guest limit.</summary>
    public const int UnlimitedCap = 99;
    /// <summary>The invitation has no limit on how many people may come (InvitedCount is 0).</summary>
    public bool NoGuestLimit => EffectiveInvited == 0;
    /// <summary>The largest headcount an answer may name.</summary>
    public int MaxHeadcount => EffectiveInvited > 0 ? EffectiveInvited : UnlimitedCap;

    /// <summary>Unguessable token used in the personal RSVP link.</summary>
    public string Token { get; set; } = "";

    public RsvpStatus Status { get; set; } = RsvpStatus.Pending;
    /// <summary>Everyone attending, adults and children together (this is what the totals use).</summary>
    public int AttendingCount { get; set; }
    /// <summary>How many of <see cref="AttendingCount"/> are children. Answers from before children were tracked count as adults.</summary>
    public int AttendingChildren { get; set; }
    /// <summary>The adults attending: everyone except the children.</summary>
    public int AttendingAdults => AttendingCount - AttendingChildren;
    public RsvpSource Source { get; set; } = RsvpSource.None;
    public DateTime? RespondedAtUtc { get; set; }

    public DateTime? InviteSentAtUtc { get; set; }
    public int RemindersSent { get; set; }
    public DateTime? LastReminderAtUtc { get; set; }
    public DateTime? ThankYouSentAtUtc { get; set; }

    public DateTime? FirstOpenedAtUtc { get; set; }
    public DateTime? LastOpenedAtUtc { get; set; }

    /// <summary>Set when this person answered through a group link (see <see cref="GroupLink"/>). Such guests are never invited or reminded by SMS.</summary>
    public int? GroupLinkId { get; set; }
    /// <summary>The group-link guest who added this person to their answer; null when the person answered for themselves.</summary>
    public int? AddedByGuestId { get; set; }
    /// <summary>A child added through a group link (their phone number is optional).</summary>
    public bool IsChild { get; set; }

    /// <summary>The family this guest belongs to (null = on their own). Members share one answer, see <see cref="Family"/>.</summary>
    public int? FamilyId { get; set; }
    public Family? Family { get; set; }

    /// <summary>
    /// Public pages only: the people all the members of the family are invited with together (0 = some member has no limit). Set by
    /// <c>RsvpService.FindAsync</c>; never stored.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public int? FamilyInvited { get; set; }
    /// <summary>What an answer may name: the whole family's invitation for a family member, otherwise this guest's own.</summary>
    public int EffectiveInvited => FamilyInvited ?? InvitedCount;

    /// <summary>The name a page or SMS greets this guest with: "Cohen family" / "משפחת כהן" for a family member, else their own name.</summary>
    public string GreetingName(bool hebrew) => Family is { } f && !string.IsNullOrWhiteSpace(f.Name)
        ? (hebrew ? $"משפחת {f.Name}" : $"{f.Name} family")
        : Name;

    /// <summary>Dietary needs of the people attending (asked on the guest page when the couple turns it on): how many of them are vegetarian, vegan, gluten-free, plus a free note.</summary>
    public int DietVegetarian { get; set; }
    public int DietVegan { get; set; }
    public int DietGlutenFree { get; set; }
    public string? DietNote { get; set; }
    public const int DietNoteMax = 300;
    public bool HasDiet => DietVegetarian > 0 || DietVegan > 0 || DietGlutenFree > 0 || !string.IsNullOrWhiteSpace(DietNote);

    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<MessageLog> Messages { get; set; } = new();

    public bool HasValidPhone => PhoneE164 is not null;
}
