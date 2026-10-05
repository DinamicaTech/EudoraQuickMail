using System;
using System.Globalization;

namespace QuickMail.Helpers;

/// <summary>Shared local-date input for Snooze and Send Later.</summary>
public static class ScheduledDateParser
{
    public static bool TryParse(string? text, CultureInfo culture, out DateTime value)
    {
        // DateOnly rejects an explicit time, including midnight: only an omitted time
        // receives the morning default. Keep DateTime's existing culture-aware parsing.
        if (DateOnly.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            value = date.ToDateTime(new TimeOnly(9, 0));
            return true;
        }
        return DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out value);
    }
}
