using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class LanguageModeTests
{
    private static EventSettings Ready(LanguageMode mode) => new()
    {
        CoupleNames = "דנה ואבי", CoupleNamesEn = "Dana & Avi",
        StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0), LanguageMode = mode,
    };

    [Fact]
    public void Hebrew_only_sends_only_the_hebrew_text()
    {
        var body = MessageTemplates.Body(Ready(LanguageMode.HebrewOnly), MessageKind.Invite, "רות", "https://x/AbCdEfGhJk");
        Assert.Contains("דנה ואבי", body);
        Assert.DoesNotContain("Dana", body);
    }

    [Fact]
    public void English_only_uses_the_english_template_and_names()
    {
        var body = MessageTemplates.Body(Ready(LanguageMode.EnglishOnly), MessageKind.Invite, "Ruth", "https://x/AbCdEfGhJk");
        Assert.Contains("Dana & Avi", body);
        Assert.DoesNotContain("דנה", body);
        Assert.True(SmsSegments.Count(body).Segments >= 1);
    }

    [Fact]
    public void Both_carries_both_texts_and_a_link_in_each()
    {
        var body = MessageTemplates.Body(Ready(LanguageMode.Both), MessageKind.Invite, "Ruth", "https://x/AbCdEfGhJk");
        Assert.Contains("דנה ואבי", body);
        Assert.Contains("Dana & Avi", body);
        Assert.Equal(2, body.Split("https://x/AbCdEfGhJk").Length - 1);
    }

    [Fact]
    public void English_text_falls_back_to_the_hebrew_names_but_still_needs_some_name()
    {
        var s = new EventSettings
        {
            LanguageMode = LanguageMode.EnglishOnly, CoupleNames = "דנה ואבי",
            StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0),
        };
        Assert.Empty(MessageTemplates.MissingFields(s, MessageKind.Invite));
        s.CoupleNames = "";
        Assert.Equal(new[] { "{couple}" }, MessageTemplates.MissingFields(s, MessageKind.Invite));
    }

    [Fact]
    public void Missing_fields_are_checked_for_every_language_that_will_be_sent()
    {
        var s = Ready(LanguageMode.Both);
        s.InviteTemplateEn = "{name}, join us at {venue}: {link}";
        Assert.Equal(new[] { "{venue}" }, MessageTemplates.MissingFields(s, MessageKind.Invite));
    }

    [Theory]
    [InlineData("#1F6F5F", true)]
    [InlineData("#abcdef", true)]
    [InlineData("red", false)]
    [InlineData("#12345", false)]
    [InlineData("#12345G", false)]
    [InlineData("#123456;background:url(x)", false)]
    [InlineData(null, false)]
    public void Only_exact_hex_colours_are_accepted_for_css(string? v, bool ok) =>
        Assert.Equal(ok, HexColor.IsValid(v));

    [Fact]
    public void Light_backgrounds_get_dark_text()
    {
        Assert.True(HexColor.IsLight("#FFFFFF"));
        Assert.False(HexColor.IsLight("#1B2A2F"));
    }
}
