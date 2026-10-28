namespace WeddingRsvp.Domain;

public enum RsvpStatus { Pending = 0, Attending = 1, Declined = 2 }

/// <summary>Where the current answer came from.</summary>
public enum RsvpSource { None = 0, Web = 1, Manual = 2 }

public enum MessageKind { Invite = 0, Reminder = 1, ThankYou = 2, Test = 3 }

/// <summary>
/// Sent (provider accepted it) -> Delivered (provider's delivery report) -> Opened (the guest opened the link, which also
/// proves delivery). A message only moves forward along that line.
/// </summary>
public enum MessageStatus { Queued = 0, Sent = 1, Delivered = 2, Failed = 3, Cancelled = 4, Opened = 5 }

public enum ThankYouAudience { AttendingOnly = 0, EveryoneInvited = 1 }

/// <summary>Which language(s) guests get: Hebrew only, English only, or both texts in one SMS and a toggle on the page.</summary>
public enum LanguageMode { HebrewOnly = 0, EnglishOnly = 1, Both = 2 }
