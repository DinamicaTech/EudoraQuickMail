using QuickMail.Services;

namespace QuickMail.Models;

public sealed record HomeAction(string Kind, Guid? AccountId = null, string? Value = null, CalendarEvent? Event = null);
public sealed record HomeNotice(string Text, string Button, string Hint, HomeAction Action, bool Error = false);
public sealed record HomeAccountStatus(Guid Id, string Name, bool CanTest, string Receiving, string Sending,
    string ReceiveColor, string SendColor, string LastReceived, string Detail, bool ReceiveOk, bool ReceiveUnknown, bool SendUnknown);
public sealed record HomeBackupStatus(string Status, string Color, string When, string Detail);
public sealed record HomeBackupData(bool Enabled, HomeBackupStatus Backup, HomeBackupStatus Restore,
    string Next, IReadOnlyList<HomeNotice> Notices);
public sealed record HomeAppointment(string Time, string Title, string Color, string Source = "", CalendarEvent? Event = null);
public sealed record HomeCalendarStatus(Guid AccountId, string Name, bool Enabled, string Status,
    string Color, string LastSuccess, string Detail)
{
    public static HomeCalendarStatus From(AccountModel account, AccountHealthObservation? health, DateTimeOffset now)
    {
        var status = !account.SyncCalendar ? "Disabled" : health == null ? "Not checked yet"
            : health.Error != null ? "Sync failed" : now-health.AttemptUtc > TimeSpan.FromDays(1) ? "Last OK >24 h" : "Last sync OK";
        var color = !account.SyncCalendar ? HomeDashboardData.Gray : health?.Error != null ? HomeDashboardData.Red
            : health == null || now-health.AttemptUtc > TimeSpan.FromDays(1) ? HomeDashboardData.Amber : HomeDashboardData.Green;
        return new(account.Id,account.AccountLabel,account.SyncCalendar,status,color,
            health?.SuccessUtc?.ToLocalTime().ToString("g")??"—",
            health?.Error == null ? "Status reflects the last recorded synchronization, not continuous monitoring."
                : health.Error + " Cached events are retained and may be out of date.");
    }
}
public sealed record HomeDashboardData(DateTime Updated, string LastDownload, IReadOnlyList<HomeAccountStatus> Accounts,
    IReadOnlyList<HomeNotice> Notices, HomeBackupData Backup, int Scheduled, int Due, int QueueErrors, int FollowUps,
    HomeMailStatistics? Statistics, string? StatisticsError, IReadOnlyList<HomeAppointment> Appointments)
{
    public IReadOnlyList<HomeCalendarStatus> Calendars { get; init; } = [];
    public string? AgendaWarning { get; init; }
    public const string Green = "#1F7A4D", Amber = "#B45309", Red = "#B42318", Gray = "#9AA5B1";
    public static (double Week, double Month) DailyAverages(IReadOnlyList<HomeMailCount> counts, DateTime today)
    {
        var daysInWeek = ((int)today.DayOfWeek + 6) % 7 + 1;
        return (counts[2].Received / (double)daysInWeek, counts[3].Received / (double)today.Day);
    }
}
