using QuickMail.Models;

namespace QuickMail.Services;

public static class RepeatedSending
{
    public const string Header = "X-QuickMail-Repetition";
    public static string Encode(RepeatedSendSchedule plan) => Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan));
    public static RepeatedSendSchedule? ReadHeaders(string raw)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw + "\r\n\r\n"));
            using var mime = MimeKit.MimeMessage.Load(stream);
            return mime.Headers[Header] is { } encoded
                ? System.Text.Json.JsonSerializer.Deserialize<RepeatedSendSchedule>(Convert.FromBase64String(encoded)) : null;
        }
        catch (FormatException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static DateTimeOffset AtEight(DateOnly date, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = date.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) throw new ArgumentException("08:00 does not exist on this date in the selected time zone.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static void Validate(RepeatedSendSchedule plan, DateTimeOffset scheduledAt)
    {
        if (plan.IntervalDays is < 1 or > 36500) throw new ArgumentException("Interval must be between 1 and 36500 days.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(scheduledAt, zone).DateTime);
        if (date > plan.EndDate || scheduledAt != AtEight(date, plan.TimeZoneId))
            throw new ArgumentException("Repetitions must be scheduled at 08:00 on or before the end date. Use Send repeatedly to change the series.");
    }

    public static DateTimeOffset? Next(RepeatedSendSchedule plan, DateTimeOffset previous, DateTimeOffset now)
    {
        Validate(plan, previous);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId);
        var prior = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(previous, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var steps = Math.Max(1, (today.DayNumber - prior.DayNumber) / plan.IntervalDays);
        var day = (long)prior.DayNumber + (long)steps * plan.IntervalDays;
        if (day > plan.EndDate.DayNumber) return null;
        var next = AtEight(DateOnly.FromDayNumber((int)day), plan.TimeZoneId);
        if (next <= now)
        {
            day += plan.IntervalDays;
            if (day > plan.EndDate.DayNumber) return null;
            next = AtEight(DateOnly.FromDayNumber((int)day), plan.TimeZoneId);
        }
        return next;
    }
}
