using System.Globalization;

namespace DinamicaBackup;

public static class ReportFormatting
{
    public static string Duration(TimeSpan? duration)
    {
        if (duration is null) return "—";
        var seconds = Math.Max(0, duration.Value.Ticks / TimeSpan.TicksPerSecond);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60);
    }
}
