using WeddingRsvp.Domain;
using WeddingRsvp.Services;

namespace WeddingRsvp.Tests;

public class MissingFieldsTests
{
    [Fact]
    public void New_settings_assume_nothing_about_the_couple()
    {
        var s = new EventSettings();
        Assert.Equal("", s.CoupleNames);
        Assert.Equal("", s.Title);
        Assert.Null(s.StartsAtLocal);
        Assert.False(s.HasStartDate);
    }

    [Fact]
    public void Default_invite_template_needs_couple_and_date()
    {
        var missing = MessageTemplates.MissingFields(new EventSettings(), MessageKind.Invite);
        Assert.Equal(new[] { "{couple}", "{date}" }, missing);
    }

    [Fact]
    public void Nothing_missing_once_couple_and_date_are_set()
    {
        var s = new EventSettings { CoupleNames = "A ו-B", StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0) };
        Assert.Empty(MessageTemplates.MissingFields(s, MessageKind.Invite));
    }

    [Fact]
    public void Venue_is_only_required_when_a_template_uses_it()
    {
        var s = new EventSettings { CoupleNames = "A ו-B", StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0) };
        Assert.Empty(MessageTemplates.MissingFields(s, MessageKind.Invite));

        s.InviteTemplate = "{name}, החתונה ב-{venue}: {link}";
        Assert.Equal(new[] { "{venue}" }, MessageTemplates.MissingFields(s, MessageKind.Invite));

        s.VenueName = "  ";
        Assert.Equal(new[] { "{venue}" }, MessageTemplates.MissingFields(s, MessageKind.Invite));

        s.VenueName = "אולם";
        Assert.Empty(MessageTemplates.MissingFields(s, MessageKind.Invite));
    }

    [Fact]
    public void Name_and_link_are_never_reported_missing()
    {
        var s = new EventSettings { InviteTemplate = "{name} {link}" };
        Assert.Empty(MessageTemplates.MissingFields(s, MessageKind.Invite));
    }

    [Fact]
    public void Calendar_outputs_are_null_without_a_start_date()
    {
        var s = new EventSettings();
        Assert.Null(CalendarLinks.GoogleCalendarUrl(s));
        Assert.Null(CalendarLinks.BuildIcs(s, "https://x", DateTime.UtcNow));
    }

    [Fact]
    public void Calendar_end_defaults_to_four_hours_after_start()
    {
        var s = new EventSettings { Title = "t", StartsAtLocal = new DateTime(2027, 3, 15, 19, 0, 0) };
        var ics = CalendarLinks.BuildIcs(s, "https://x", DateTime.UtcNow)!;
        Assert.Contains("DTEND:", ics);
        Assert.Equal(4, (IsraelClock.ToUtc(s.StartsAtLocal!.Value.AddHours(4)) - IsraelClock.ToUtc(s.StartsAtLocal.Value)).TotalHours);
    }
}
