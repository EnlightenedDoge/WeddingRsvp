using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("050-1234567", "+972501234567")]
    [InlineData("0501234567", "+972501234567")]
    [InlineData("050 123 4567", "+972501234567")]
    [InlineData("501234567", "+972501234567")]        // Excel dropped the leading zero
    [InlineData("+972501234567", "+972501234567")]
    [InlineData("+972 050-123-4567", "+972501234567")] // stray 0 after country code
    [InlineData("00972501234567", "+972501234567")]
    [InlineData("+1 (415) 555-0100", "+14155550100")]  // guest abroad
    public void Normalizes_valid_numbers(string raw, string expected) =>
        Assert.Equal(expected, PhoneNormalizer.Normalize(raw).E164);

    [Theory]
    [InlineData("02-6234567")]   // Jerusalem landline
    [InlineData("03-1234567")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_non_mobile_or_garbage(string? raw)
    {
        var r = PhoneNormalizer.Normalize(raw);
        Assert.False(r.IsValid);
        Assert.NotNull(r.Error);
    }
}
