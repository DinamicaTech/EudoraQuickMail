namespace QuickMail.Models;

public sealed record RepeatedSendSchedule(int IntervalDays, DateOnly EndDate, string TimeZoneId);
public sealed record RepeatedSendRequest(DateOnly StartDate, int IntervalDays, DateOnly EndDate);
