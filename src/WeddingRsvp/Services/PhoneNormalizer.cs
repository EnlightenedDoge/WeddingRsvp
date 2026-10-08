namespace WeddingRsvp.Services;

public sealed record PhoneResult(string? E164, string? Error)
{
    public bool IsValid => E164 is not null;
}

/// <summary>
/// Turns whatever the client typed into E.164. Israeli mobiles (05X) are the main case;
/// explicit international numbers (+X / 00X) are accepted as-is. Israeli landlines are rejected
/// because they can't receive SMS.
/// </summary>
public static class PhoneNormalizer
{
    public static PhoneResult Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, "חסר מספר");

        var s = raw.Trim();
        var international = s.StartsWith('+') || s.StartsWith("00");
        var digits = new string(s.Where(char.IsAsciiDigit).ToArray());
        if (s.StartsWith("00")) digits = digits[2..];

        string national;
        if (digits.StartsWith("972"))
        {
            national = digits[3..];
            if (national.StartsWith('0')) national = national[1..]; // "+972 050..." typo
        }
        else if (international)
        {
            return digits.Length is >= 8 and <= 15
                ? new("+" + digits, null)
                : new(null, "מספר בינלאומי לא תקין");
        }
        else if (digits.StartsWith('0'))
        {
            national = digits[1..];
        }
        else
        {
            national = digits; // Excel likes to drop the leading zero: 501234567
        }

        if (national.Length == 9 && national[0] == '5') return new("+972" + national, null);
        if (national.Length is 8 or 9) return new(null, "לא מספר נייד");
        return new(null, "מספר לא תקין");
    }
}
