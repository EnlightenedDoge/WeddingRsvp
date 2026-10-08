namespace WeddingRsvp.Domain;

/// <summary>
/// One invitation (a household / couple / single guest), reachable on one phone number.
/// InvitedCount is how many people this invitation covers.
/// All *Utc properties are stored and read as UTC (see AppDbContext).
/// </summary>
public class Guest : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Phone as it appeared in the client's list.</summary>
    public string PhoneRaw { get; set; } = "";

    /// <summary>Normalized E.164 number, or null when the raw value couldn't be normalized.</summary>
    public string? PhoneE164 { get; set; }
    public string? PhoneError { get; set; }

    public int InvitedCount { get; set; } = 1;

    /// <summary>Unguessable token used in the personal RSVP link.</summary>
    public string Token { get; set; } = "";

    public RsvpStatus Status { get; set; } = RsvpStatus.Pending;
    public int AttendingCount { get; set; }
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
