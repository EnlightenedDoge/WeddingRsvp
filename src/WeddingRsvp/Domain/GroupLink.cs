namespace WeddingRsvp.Domain;

/// <summary>
/// One shared link for a whole group (for example a WhatsApp group). Everyone who opens it enters a phone number and name; whoever
/// answers becomes a guest (see <see cref="Guest.GroupLinkId"/>). Open until <see cref="ExpiresAtLocal"/>, or until the event starts when
/// there is no expiry, unless the couple turned it off.
/// </summary>
public class GroupLink : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Short unguessable code in the link (/g/{code}). Globally unique.</summary>
    public string Code { get; set; } = "";
    /// <summary>Most people one answer may cover, the person answering included.</summary>
    public int MaxGuests { get; set; } = 4;
    /// <summary>0 in <see cref="MaxGuests"/> means no guest limit (answers may name up to <see cref="Guest.UnlimitedCap"/>), like an invitation with no limit.</summary>
    public bool NoGuestLimit => MaxGuests <= 0;
    /// <summary>The most people one answer may cover.</summary>
    public int EffectiveMax => NoGuestLimit ? Guest.UnlimitedCap : MaxGuests;
    /// <summary>Israel local time; null means open until the event starts.</summary>
    public DateTime? ExpiresAtLocal { get; set; }
    public bool IsOff { get; set; }
    /// <summary>SMS phone verification for this link (a toggle per link): off, only when an existing answer is opened or taken over, or on every visit. Each code costs the couple 1 SMS credit.</summary>
    public GroupVerifyMode VerifyMode { get; set; } = GroupVerifyMode.Off;
    public DateTime CreatedAtUtc { get; set; }

    public const int MaxGuestsLimit = 50;
}

public enum GroupLinkStatus { Active, Expired, Off }
