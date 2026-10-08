namespace WeddingRsvp.Domain;

public enum RsvpStatus { Pending = 0, Attending = 1, Declined = 2 }

/// <summary>Where the current answer came from.</summary>
public enum RsvpSource { None = 0, Web = 1, Manual = 2 }

public enum MessageKind { Invite = 0, Reminder = 1, ThankYou = 2, Test = 3 }

public enum MessageStatus { Queued = 0, Sent = 1, Delivered = 2, Failed = 3, Cancelled = 4 }

public enum ThankYouAudience { AttendingOnly = 0, EveryoneInvited = 1 }
