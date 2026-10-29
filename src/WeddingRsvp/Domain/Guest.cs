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
    public bool NoGuestLimit => InvitedCount == 0;
    /// <summary>The largest headcount an answer may name.</summary>
    public int MaxHeadcount => InvitedCount > 0 ? InvitedCount : UnlimitedCap;

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

    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<MessageLog> Messages { get; set; } = new();

    public bool HasValidPhone => PhoneE164 is not null;
}
