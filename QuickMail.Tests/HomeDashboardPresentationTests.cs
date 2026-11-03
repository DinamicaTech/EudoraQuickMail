using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Views;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public sealed class HomeDashboardPresentationTests : WpfTestBase
{
    private static HomeDashboardData Example()
    {
        var accounts=Enumerable.Range(1,8).Select(i=>new HomeAccountStatus(Guid.NewGuid(),$"account{i}@example.test",true,
            i<7?"Last check OK":"Not checked yet","Not checked yet",i<7?HomeDashboardData.Green:HomeDashboardData.Amber,
            HomeDashboardData.Amber,i<7?"Today 15:06":"—","",i<7,i>=7,true)).ToList();
        var counts=new List<HomeMailCount>{new("Today",10,2,3),new("Yesterday",8,0,0),new("This week",131,12,8),new("This month",349,36,21),new("All dates (pending)",0,0,28)};
        var stats=new HomeMailStatistics(accounts.ToDictionary(a=>a.Id,a=>(IReadOnlyList<HomeMailCount>)counts),
            [new(accounts[0].Id,"Providers","Providers",23,false),new(accounts[0].Id,"Junk","Junk",11,false),new(accounts[0].Id,"Draft","Draft",79,true)]);
        var backup=new HomeBackupData(true,new("Success",HomeDashboardData.Green,"Today 00:05 · took 04:55:00",""),
            new("Failed",HomeDashboardData.Red,"Today 05:00 · took 00:00:06","No restore-test folder chosen"),"Tomorrow 00:01",[]);
        return new(new DateTime(2026,10,18,15,8,0),"15:07",accounts,
            [new("Restore test failed: no restore-test folder has been chosen, so the backup could not be checked.","Choose restore-test folder…","Open settings",new("backup",Value:"Restore test"),true),
             new("Sending has not been tested on any of your 8 accounts. A wrong SMTP setting could go unnoticed.","Test sending on all accounts","Test SMTP",new("test-sending")),
             new("2 accounts have no successful receive check recorded in this version.","Check these accounts now","Check incoming",new("receive-unknown"))],
             backup,19,0,0,0,stats,null,[]);
    }
    [WpfFact]
    public void NativeDashboardRendersAndRoutesActionsWithoutOpeningWindows()
    {
        var view=new HomeDashboardView();view.Render(Example());
        view.Measure(new Size(1440,1400));view.Arrange(new Rect(0,0,1440,1400));view.UpdateLayout();
        HomeAction? requested=null;view.ActionRequested+=a=>requested=a;
        var buttons=Children<Button>(view).ToList();
        var test=buttons.First(b=>Equals(b.Content,"Test"));test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("test",requested?.Kind);Assert.NotNull(requested?.AccountId);
        buttons.First(b=>Equals(b.Content,"History…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("History",requested?.Value);
        view.SetBusy(true,"Testing…");Assert.All(buttons,b=>Assert.False(b.IsEnabled));
        view.SetBusy(false,"Complete");
        var output=Environment.GetEnvironmentVariable("EQM_HOME_PREVIEW_DIR");
        if(!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            Render(view,1440,1400,Path.Combine(output,"home-wide.png"));
            Render(view,820,1600,Path.Combine(output,"home-narrow.png"));
        }
    }
    private static void Render(FrameworkElement view,int width,int height,string path)
    {
        view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream=File.Create(path);encoder.Save(stream);
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=VisualTreeHelper.GetChild(parent,i);if(child is T t)yield return t;foreach(var nested in Children<T>(child))yield return nested;}
    }
    [Fact]
    public void DailyAveragesUseElapsedCalendarDaysIncludingToday()
    {
        var stats=Example().Statistics!.ForAccount(Example().Accounts[0].Id); // absent account: explicit zeroes
        Assert.All(stats,r=>Assert.Equal(0,r.Received));
        var counts=new[]{new HomeMailCount("Today",10,0,0),new("Yesterday",0,0,0),new("This week",131,0,0),new("This month",349,0,0)};
        var values=HomeDashboardData.DailyAverages(counts,new DateTime(2026,10,18));
        Assert.Equal(131d/7,values.Week);Assert.Equal(349d/18,values.Month);
    }

    [WpfFact]
    public void RefreshAndAccountSelectionCanRebuildAnAlreadyRenderedDashboard()
    {
        var data=Example();
        var view=new HomeDashboardView();
        view.Render(data with { Statistics=null });
        view.Measure(new Size(1440,1400));view.Arrange(new Rect(0,0,1440,1400));view.UpdateLayout();
        view.Render(data);
        view.UpdateLayout();
        var selector=Children<ComboBox>(view).Single();
        selector.SelectedIndex=1;
        view.UpdateLayout();
        view.Render(data with { Updated=data.Updated.AddMinutes(5) });
        view.UpdateLayout();
        Assert.Contains(Children<TextBlock>(view),t=>t.Text=="Home");
        Assert.Contains(Children<Button>(view),b=>Equals(b.Content,"Refresh"));
        Assert.Equal(1,Children<ComboBox>(view).Single().SelectedIndex);
        view.SetBusy(false,"Refreshed successfully");
        Assert.Contains(Children<TextBlock>(view),t=>t.Text=="Refreshed successfully");
    }

    [Fact]
    public void CalendarStatusDistinguishesDisabledUnknownFailedAndStale()
    {
        var account=new AccountModel {Id=Guid.NewGuid(),SyncCalendar=true};
        var now=DateTimeOffset.UtcNow;
        Assert.Equal("Not checked yet",HomeCalendarStatus.From(account,null,now).Status);
        var ok=new AccountHealthObservation(account.Id,"Calendar",now,now,null);
        Assert.Equal(HomeDashboardData.Green,HomeCalendarStatus.From(account,ok,now).Color);
        Assert.Equal("Last OK >24 h",HomeCalendarStatus.From(account,ok,now.AddDays(2)).Status);
        var failed=HomeCalendarStatus.From(account,ok with {Error="Unauthorized"},now);
        Assert.Equal(HomeDashboardData.Red,failed.Color);
        Assert.NotEqual("—",failed.LastSuccess);
        Assert.Contains("Cached events",failed.Detail);
        account.SyncCalendar=false;
        Assert.Equal("Disabled",HomeCalendarStatus.From(account,ok,now).Status);
    }

    [WpfFact]
    public void CalendarCardRetriesOnlyOnClickAndPreservesCachedAgenda()
    {
        var account=Guid.NewGuid();
        var evt=new CalendarEvent {AccountId=account,Summary="Cached appointment"};
        var data=Example() with
        {
            Calendars=[new(account,"Calendar account",true,"Sync failed",HomeDashboardData.Red,"Yesterday","Cached data")],
            Appointments=[new("All day",evt.Summary,HomeDashboardData.Green,"Calendar account",evt)],
            AgendaWarning="Events may be out of date."
        };
        var view=new HomeDashboardView();HomeAction? action=null;view.ActionRequested+=a=>action=a;
        view.Render(data);view.Measure(new Size(1440,2400));view.Arrange(new Rect(0,0,1440,2400));view.UpdateLayout();
        Assert.Null(action);
        Assert.Contains(Children<TextBlock>(view),t=>t.Text=="Cached appointment");
        Assert.Contains(Children<TextBlock>(view),t=>t.Text=="Events may be out of date.");
        Children<Button>(view).Single(b=>Equals(b.Content,"Sync now")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("calendar-sync",action?.Kind);Assert.Equal(account,action?.AccountId);
        var output=Environment.GetEnvironmentVariable("EQM_HOME_PREVIEW_DIR");
        if(!string.IsNullOrWhiteSpace(output))
        { Directory.CreateDirectory(output);Render(view,1440,2400,Path.Combine(output,"home-calendar.png")); }
    }
}
