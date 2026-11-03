namespace WeddingRsvp.Domain;

/// <summary>One SMS code sent to prove someone holds a phone number (group links). Only a salted hash of the code is kept.</summary>
public class PhoneVerification : ITenantOwned
{
    public int Id { get; set; }
    public int CoupleId { get; set; }
    public string PhoneE164 { get; set; } = "";
    public string Salt { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}
