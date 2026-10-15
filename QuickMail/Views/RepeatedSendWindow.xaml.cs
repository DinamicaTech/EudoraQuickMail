using System.Windows;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class RepeatedSendWindow : Window
{
    public RepeatedSendRequest? Request { get; private set; }
    public RepeatedSendWindow(RepeatedSendRequest? suggested)
    {
        InitializeComponent();
        var first = DateTime.Now.Hour < 8 ? DateTime.Today : DateTime.Today.AddDays(1);
        StartDate.Value = suggested?.StartDate.ToDateTime(TimeOnly.MinValue) ?? first;
        EndDate.Value = suggested?.EndDate.ToDateTime(TimeOnly.MinValue) ?? first.AddDays(30);
        Interval.Text = (suggested?.IntervalDays ?? 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        Loaded += (_, _) => StartDate.Focus();
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StartDate.Value is not DateTime start || EndDate.Value is not DateTime end || !int.TryParse(Interval.Text, out var days))
                throw new ArgumentException("Enter valid start/end dates and a whole number of days.");
            var request = new RepeatedSendRequest(DateOnly.FromDateTime(start), days, DateOnly.FromDateTime(end));
            var plan = new RepeatedSendSchedule(days, request.EndDate, TimeZoneInfo.Local.Id);
            var when = RepeatedSending.AtEight(request.StartDate, plan.TimeZoneId);
            RepeatedSending.Validate(plan, when);
            if (when <= DateTimeOffset.UtcNow) throw new ArgumentException("The first sending date at 08:00 must be in the future.");
            Request = request;
            Close();
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
    }
}
