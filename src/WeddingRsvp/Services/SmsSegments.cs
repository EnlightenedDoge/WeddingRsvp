namespace WeddingRsvp.Services;

public readonly record struct SegmentInfo(int Segments, bool Unicode, int Length);

/// <summary>
/// Estimates SMS segment count. Any Hebrew character forces UCS-2: 70 chars in one segment,
/// 67 per segment when split. Providers bill per segment.
/// </summary>
public static class SmsSegments
{
    private const string Gsm7 =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡" +
        "ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Extended = "^{}\\[~]|€";

    public static SegmentInfo Count(string text)
    {
        var isGsm = text.All(c => Gsm7.Contains(c) || Gsm7Extended.Contains(c));
        if (isGsm)
        {
            var len = text.Sum(c => Gsm7Extended.Contains(c) ? 2 : 1);
            return new(len <= 160 ? 1 : (int)Math.Ceiling(len / 153.0), false, len);
        }
        var l = text.Length; // UTF-16 code units == UCS-2 units
        return new(l <= 70 ? 1 : (int)Math.Ceiling(l / 67.0), true, l);
    }
}
