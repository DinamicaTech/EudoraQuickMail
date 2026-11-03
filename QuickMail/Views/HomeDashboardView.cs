using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>Native, responsive implementation of the supplied Home design (no HTML/runtime execution).</summary>
public sealed class HomeDashboardView : UserControl
{
    private readonly StackPanel _page = new() { MaxWidth = 1400, Margin = new Thickness(28,24,28,32) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _status = Text("Loading local summaries…",12,"#5B6675");
    private readonly UniformGrid _tiles = new();
    private readonly Grid _columns = new();
    private StackPanel? _left, _right;
    private HomeDashboardData? _data;
    private Guid? _account;
    private bool _busy;
    public event Action<HomeAction>? ActionRequested;
    public HomeDashboardView()
    {
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Foreground = Brush("#1F2933");
        Background = Brush("#EEF1F5");
        _scroll.Content = _page; Content = _scroll;
        _page.Children.Add(Text("Home",26)); _page.Children.Add(_status);
        SizeChanged += (_, _) => ArrangeColumns();
        _page.SizeChanged += (_, _) => ArrangeColumns();
    }
    private static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(value)!;
    private static TextBlock Text(string text,double size=13,string color="#1F2933",bool bold=false) => new()
    {
        Text=text,FontSize=size,Foreground=Brush(color),FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,
        TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center
    };
    private static Border Card(UIElement content, double padding=20, string background="#FFFFFF",string border="#D9DEE5") => new()
    {
        Child=content,Background=Brush(background),BorderBrush=Brush(border),BorderThickness=new Thickness(1),
        CornerRadius=new CornerRadius(10),Padding=new Thickness(padding),Margin=new Thickness(0,0,0,20)
    };
    private static StackPanel Stack(params UIElement[] children)
    {
        var panel=new StackPanel(); foreach(var child in children) panel.Children.Add(child); return panel;
    }
    private static FrameworkElement Space(double height=12) => new Border { Height=height };
    private Button Button(string text,HomeAction action,string tip,bool primary=false)
    {
        var b=new Button { Content=text,FontSize=13,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,0),
            Foreground=Brush(primary?"#FFFFFF":"#1F2933"),Background=Brush(primary?"#1D4ED8":"#FFFFFF"),
            BorderBrush=Brush(primary?"#1D4ED8":"#C3CAD4"),BorderThickness=new Thickness(1),ToolTip=tip,
            Cursor=System.Windows.Input.Cursors.Hand,IsEnabled=!_busy,Tag=true };
        var frame=new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
        frame.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
        frame.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);
        frame.AppendChild(presenter); b.Template=new ControlTemplate(typeof(Button)){VisualTree=frame};
        b.Click+=(_,_)=>ActionRequested?.Invoke(action);
        b.MouseEnter+=(_,_)=>b.Background=Brush(primary?"#1E40AF":"#F3F5F8");
        b.MouseLeave+=(_,_)=>b.Background=Brush(primary?"#1D4ED8":"#FFFFFF");
        return b;
    }
    private static FrameworkElement Dot(string color) => new Ellipse { Width=9,Height=9,Fill=Brush(color),Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center };
    private static FrameworkElement Signal(string text,string color)
    {
        var p=new DockPanel(); var dot=Dot(color); DockPanel.SetDock(dot,Dock.Left);p.Children.Add(dot);p.Children.Add(Text(text));return p;
    }
    private static DockPanel Heading(string text,UIElement? actions=null)
    {
        var p=new DockPanel { Margin=new Thickness(0,0,0,14),LastChildFill=true };
        if(actions!=null){DockPanel.SetDock(actions,Dock.Right);p.Children.Add(actions);}
        p.Children.Add(Text(text,17,bold:true));return p;
    }
    private static WrapPanel Buttons(params UIElement[] buttons)
    {
        var p=new WrapPanel();foreach(var b in buttons)p.Children.Add(b);return p;
    }
    public void SetBusy(bool busy,string message)
    {
        _busy=busy;
        // Keep the last snapshot on screen while reloading; do not reset scroll or focus.
        _status.Text=message;
        foreach(var b in Descendants<Button>(this)) b.IsEnabled=!busy && b.Tag is not false;
        foreach(var c in Descendants<ComboBox>(this)) c.IsEnabled=!busy;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var c=VisualTreeHelper.GetChild(parent,i);if(c is T found)yield return found;foreach(var d in Descendants<T>(c))yield return d;}
    }
    public void Render(HomeDashboardData data)
    {
        // Clearing the page detaches the header, not its nested children. Release the
        // persistent status control before attaching it to the replacement header.
        if (_status.Parent is Panel previousHeader) previousHeader.Children.Remove(_status);
        _data=data; var offset=_scroll.VerticalOffset;_page.Children.Clear();_tiles.Children.Clear();_columns.Children.Clear();
        var header=new DockPanel { Margin=new Thickness(0,0,0,20) };
        var refresh=Button("Refresh",new("refresh"),"Re-read local account, backup and mail summaries now.");
        var right=Buttons(_status,refresh);_status.Margin=new Thickness(0,0,12,0);
        _status.Text=$"Updated {data.Updated:t} · last download {data.LastDownload} · refreshes every 5 min";
        DockPanel.SetDock(right,Dock.Right);header.Children.Add(right);
        header.Children.Add(Stack(Text(data.Updated.ToString("dddd, d MMMM yyyy"),13,"#5B6675"),Text("Home",26,bold:true)));
        _page.Children.Add(header);
        var notices=Stack(Text(data.Notices.Count==0?"No recorded issues":$"{data.Notices.Count} things need your attention",17,data.Notices.Count==0?HomeDashboardData.Green:"#7A3E06",true));
        if(data.Notices.Count==0)notices.Children.Add(Text("Only recorded operations are known. Untested connections are not assumed to work.",12,"#5B6675"));
        foreach(var n in data.Notices)
        {
            var row=new DockPanel();var b=Button(n.Button,n.Action,n.Hint,n.Error);DockPanel.SetDock(b,Dock.Right);row.Children.Add(b);
            var dot=Dot(n.Error?HomeDashboardData.Red:HomeDashboardData.Amber);DockPanel.SetDock(dot,Dock.Left);row.Children.Add(dot);
            row.Children.Add(Text(n.Text,14));var card=Card(row,12,border:"#F1D19A");card.CornerRadius=new CornerRadius(8);card.Margin=new Thickness(0,10,0,0);notices.Children.Add(card);
        }
        _page.Children.Add(Card(notices,18,data.Notices.Count==0?"#F0F8F3":"#FFF8EB",data.Notices.Count==0?"#C8E4D3":"#F1D19A"));
        var active=data.Accounts.Where(a=>a.CanTest).ToList();var ok=active.Count(a=>a.ReceiveOk);var unknown=active.Count(a=>a.ReceiveUnknown);
        _tiles.Children.Add(Tile("ACCOUNTS",$"{ok} / {active.Count}","receiving: last operation OK",$"{active.Count(a=>a.ReceiveColor==HomeDashboardData.Red)} with errors · {unknown} not checked yet",HomeDashboardData.Green));
        _tiles.Children.Add(Tile("LAST BACKUP",data.Backup.Backup.Status,data.Backup.Backup.When,"Next: "+data.Backup.Next,data.Backup.Backup.Color));
        _tiles.Children.Add(Tile("RESTORE TEST",data.Backup.Restore.Status,data.Backup.Restore.When,data.Backup.Restore.Detail,data.Backup.Restore.Color));
        _tiles.Children.Add(Tile("OUTGOING MAIL",data.Scheduled.ToString("N0"),$"scheduled for later · {data.Due:N0} due now · {data.QueueErrors:N0} errors",$"{data.FollowUps:N0} waiting for follow-up",data.QueueErrors>0?HomeDashboardData.Red:"#1F2933"));
        _page.Children.Add(_tiles);
        _left=new StackPanel();_right=new StackPanel();_columns.Children.Add(_left);_columns.Children.Add(_right);_page.Children.Add(_columns);
        _left.Children.Add(AccountsCard(data));_left.Children.Add(ActivityCard(data));
        _right.Children.Add(BackupCard(data.Backup));
        var calendars=Stack(Heading("Google Calendar"));
        if(data.Calendars.Count==0)calendars.Children.Add(Text("No Google Calendar accounts configured.",13,"#5B6675"));
        foreach(var c in data.Calendars)
        {
            var row=Stack(Text(c.Name,13,bold:true),Space(5),Signal(c.Status,c.Color),Space(5),
                Text("Last successful sync: "+c.LastSuccess,12,"#5B6675"),Space(5),Text(c.Detail,12,"#5B6675"));
            row.Children.Add(Space(8));
            row.Children.Add(c.Enabled?Button("Sync now",new("calendar-sync",c.AccountId),"Retry this account's calendar synchronization. Does not send or download mail.")
                :Button("Configure…",new("account",c.AccountId),"Open this account's calendar settings."));
            calendars.Children.Add(new Border{Child=row,Padding=new Thickness(0,10,0,10),BorderBrush=Brush("#EDF0F3"),BorderThickness=new Thickness(0,0,0,1)});
        }
        _right.Children.Add(Card(calendars));
        var today=Stack(Heading("Today",Button("New appointment",new("appointment"),"Create a calendar appointment.")));
        if(data.AgendaWarning!=null){today.Children.Add(Text(data.AgendaWarning,12,HomeDashboardData.Amber));today.Children.Add(Space(8));}
        if(data.Appointments.Count==0)today.Children.Add(Text(data.AgendaWarning==null?"No appointments today.":"No cached appointments for today.",13,"#5B6675"));
        foreach(var a in data.Appointments)
        {
            var row=new Border{BorderBrush=Brush(a.Color),BorderThickness=new Thickness(3,0,0,0),Padding=new Thickness(10,6,0,6),Margin=new Thickness(0,0,0,6),
                Background=Brush("#F5F7FA"),ToolTip="Double-click to open · "+a.Source,
                Child=Stack(Text(a.Time,12,"#5B6675"),Text(a.Title,14,a.Color,true),Text(a.Source,12,"#5B6675"))};
            row.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==2&&!_busy&&a.Event!=null)ActionRequested?.Invoke(new("open-appointment",Event:a.Event));};
            today.Children.Add(row);
        }
        _right.Children.Add(Card(today));
        var folders=Stack(Heading("Folders to review"));
        if(data.Statistics==null)folders.Children.Add(Text("Folder counts unavailable.",13,"#5B6675"));
        else if(data.Statistics.Folders.Count==0)folders.Children.Add(Text("No pending folders.",13,"#5B6675"));
        foreach(var f in data.Statistics?.Folders??[])
        {
            var row=new DockPanel { Margin=new Thickness(0,0,0,10) };
            var b=Button("Open",new(f.Drafts?"drafts":"folder",f.AccountId,f.Folder),f.Drafts?"Open the drafts in this folder.":"Open this folder with the unread filter.");
            DockPanel.SetDock(b,Dock.Right);row.Children.Add(b);
            var count=Text(f.Count.ToString("N0"),13,"#5B6675");count.Margin=new Thickness(8,0,12,0);DockPanel.SetDock(count,Dock.Right);row.Children.Add(count);
            var name=Text(f.DisplayName+(f.Drafts?" · drafts":" · unread"));name.ToolTip=data.Accounts.FirstOrDefault(a=>a.Id==f.AccountId)?.Name+" / "+f.Folder;row.Children.Add(name);folders.Children.Add(row);
        }
        _right.Children.Add(Card(folders));ArrangeColumns();_scroll.ScrollToVerticalOffset(offset);
    }
    private Border Tile(string title,string number,string detail,string foot,string color)
        =>Card(Stack(Text(title,12,"#5B6675",true),Space(8),Text(number,26,color,true),Space(5),Text(detail),Space(6),Text(foot,12,"#5B6675")),18);

    private Border AccountsCard(HomeDashboardData data)
    {
        var p=Stack(Heading("Accounts",Buttons(Button("Test all",new("test-all"),"Test receiving and sending on all active accounts. No email will be sent."),Button("Manage accounts…",new("accounts"),"Add, remove or edit accounts."))));
        var table=new Grid();double[] widths=[2.2,1.3,1.4,1.25,.85];foreach(var width in widths)table.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(width,GridUnitType.Star)});table.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        void Cell(UIElement value,int row,int col,bool head=false)
        {
            var border=new Border{Child=value,Padding=new Thickness(6,9,6,9),BorderBrush=Brush(head?"#D9DEE5":"#EDF0F3"),BorderThickness=new Thickness(0,0,0,1)};
            Grid.SetRow(border,row);Grid.SetColumn(border,col);table.Children.Add(border);
        }
        table.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var headers=new[]{"Account","Receiving","Last received OK","Sending","Today in / out",""};for(var i=0;i<headers.Length;i++)Cell(Text(headers[i],12,"#5B6675",true),0,i,true);
        var row=1;foreach(var a in data.Accounts)
        {
            table.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var name=Text(a.Name);name.TextTrimming=TextTrimming.CharacterEllipsis;name.TextWrapping=TextWrapping.NoWrap;name.ToolTip=a.Name+"\n"+a.Detail;
            name.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==2)ActionRequested?.Invoke(new("account",a.Id));};Cell(name,row,0);
            var receive=Signal(a.Receiving,a.ReceiveColor);receive.ToolTip=a.Detail;Cell(receive,row,1);
            Cell(Text(a.LastReceived,12,"#5B6675"),row,2);var send=Signal(a.Sending,a.SendColor);send.ToolTip=a.Detail;Cell(send,row,3);
            var counts=data.Statistics?.ForAccount(a.Id)[0];Cell(Text(counts==null?"—":$"{counts.Received:N0} / {counts.Sent:N0}",12,"#5B6675"),row,4);
            var test=Button("Test",new("test",a.Id),"Test receiving and sending on this account and show diagnostics. No email will be sent.");test.Tag=a.CanTest;test.IsEnabled=a.CanTest&&!_busy;Cell(test,row,5);row++;
        }
        p.Children.Add(table);p.Children.Add(Space());p.Children.Add(Text("Status reflects the last real operation or test, not continuous monitoring. History starts with this version.",12,"#5B6675"));return Card(p);
    }
    private Border ActivityCard(HomeDashboardData data)
    {
        var selector=new ComboBox{Width=210,FontSize=13,Padding=new Thickness(6),IsEnabled=!_busy};
        selector.Items.Add(new ComboBoxItem{Content="All accounts",Tag=null});foreach(var a in data.Accounts)selector.Items.Add(new ComboBoxItem{Content=a.Name,Tag=a.Id});
        selector.SelectedItem=selector.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>Equals(x.Tag,_account))??selector.Items[0];
        _account=(selector.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        selector.SelectionChanged+=(_,_)=>{_account=(selector.SelectedItem as ComboBoxItem)?.Tag as Guid?;if(_data!=null)Render(_data);};
        var p=Stack(Heading("Mail activity",selector));
        if(data.Statistics==null){p.Children.Add(Text(data.StatisticsError??"Counting local summary records…",13,HomeDashboardData.Amber));return Card(p);}
        var counts=data.Statistics.ForAccount(_account);var today=counts[0];var yesterday=counts[1];
        var kpi=new UniformGrid{Columns=3};
        foreach(var v in new[]{("RECEIVED TODAY",today.Received,$"{yesterday.Received:N0} yesterday"),("SENT TODAY",today.Sent,$"{yesterday.Sent:N0} yesterday"),("UNREAD",counts[4].Unread,$"{today.Unread:N0} arrived today")})
        {var tile=Card(Stack(Text(v.Item1,11,"#5B6675",true),Text(v.Item2.ToString("N0"),26,bold:true),Text(v.Item3,12,"#5B6675")),12,"#F5F7FA","#F5F7FA");tile.Margin=new Thickness(0,0,8,14);kpi.Children.Add(tile);}p.Children.Add(kpi);
        p.Children.Add(Text("Received per day: today against the average",13,bold:true));p.Children.Add(Space());
        var averages=HomeDashboardData.DailyAverages(counts,data.Updated.Date);var max=Math.Max(1,Math.Max(today.Received,Math.Max(averages.Week,averages.Month)));
        foreach(var bar in new[]{("Today",(double)today.Received,"#1D4ED8"),("Daily average this week",averages.Week,"#93A8D6"),("Daily average this month",averages.Month,"#93A8D6")})
        {
            var g=new Grid{Margin=new Thickness(0,0,0,10)};g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(165)});g.ColumnDefinitions.Add(new ColumnDefinition());g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(48)});
            g.Children.Add(Text(bar.Item1,12,"#3E4A59"));var track=new Grid{Height=14,Background=Brush("#EDF0F3"),Margin=new Thickness(8,0,8,0)};
            track.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(bar.Item2/max,GridUnitType.Star)});track.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1-bar.Item2/max,GridUnitType.Star)});track.Children.Add(new Border{Background=Brush(bar.Item3),CornerRadius=new CornerRadius(4)});Grid.SetColumn(track,1);g.Children.Add(track);
            var value=Text(bar.Item2.ToString(bar.Item1=="Today"?"N0":"N1"),13,bold:true);value.TextAlignment=TextAlignment.Right;Grid.SetColumn(value,2);g.Children.Add(value);p.Children.Add(g);
        }
        var table=new Grid();foreach(var width in new[]{2,1,1,1})table.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(width,GridUnitType.Star)});
        for(var i=0;i<5;i++)
        {
            table.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});var cells=i==0?new[]{"Period","Received","Sent","Unread"}:new[]{counts[i-1].Period,counts[i-1].Received.ToString("N0"),counts[i-1].Sent.ToString("N0"),counts[i-1].Unread.ToString("N0")};
            for(var c=0;c<4;c++){var t=Text(cells[c],i==0?12:13,i==0?"#5B6675":"#1F2933",i==0);t.TextAlignment=c==0?TextAlignment.Left:TextAlignment.Right;var cell=new Border{Child=t,Padding=new Thickness(0,8,0,8),BorderBrush=Brush("#EDF0F3"),BorderThickness=new Thickness(0,0,0,1)};Grid.SetRow(cell,i);Grid.SetColumn(cell,c);table.Children.Add(cell);}
        }
        p.Children.Add(table);p.Children.Add(Space());p.Children.Add(Buttons(Button("Received today",new("search",Value:"D:today;I"),"Open messages received today in all accounts."),Button("Sent today",new("search",Value:"D:today;O"),"Open messages sent today in all accounts."),Button("Unread",new("unread"),"All accounts, excluding Trash, Junk and RecoveryDeleted.")));
        p.Children.Add(Space());p.Children.Add(Text("Counts locally stored messages. Gmail label copies count once; drafts and scheduled mail are excluded. Averages include elapsed calendar days (including today). Search shortcuts use all accounts.",12,"#5B6675"));return Card(p);
    }
    private Border BackupCard(HomeBackupData backup)
    {
        var badge=new Border{Background=Brush(backup.Enabled?"#E6F4EC":"#F5F7FA"),CornerRadius=new CornerRadius(12),Padding=new Thickness(10,3,10,3),Child=Text(backup.Enabled?"Native backup on":"Native backup off",12,backup.Enabled?HomeDashboardData.Green:"#5B6675")};
        var p=Stack(Heading("Backup",badge));
        foreach(var item in new[]{("Backup",backup.Backup),("Restore test",backup.Restore),("Next backup",new HomeBackupStatus("",HomeDashboardData.Gray,backup.Next,""))})
        {var row=Stack(Signal(item.Item1+" "+item.Item2.Status,item.Item2.Color),Space(5),Text(item.Item2.When,12,"#5B6675"));if(!string.IsNullOrEmpty(item.Item2.Detail))row.Children.Add(Text(item.Item2.Detail,12,"#5B6675"));p.Children.Add(new Border{Child=row,Padding=new Thickness(0,10,0,10),BorderBrush=Brush("#EDF0F3"),BorderThickness=new Thickness(0,0,0,1)});}
        p.Children.Add(Space());p.Children.Add(Text("External backup windows only release the database; they do not confirm that the external backup worked.",12,"#5B6675"));p.Children.Add(Space());p.Children.Add(Buttons(Button("Backup settings…",new("backup",Value:"Native Backup"),"Open backup schedule and destination settings."),Button("History…",new("backup",Value:"History"),"Open the backup agent's history tab.")));return Card(p);
    }
    private void ArrangeColumns()
    {
        var available=Math.Max(300,_page.ActualWidth>0?_page.ActualWidth:Math.Min(1400,ActualWidth-56));var tileColumns=available>=1000?4:available>=520?2:1;
        _tiles.Columns=tileColumns;
        for(var i=0;i<_tiles.Children.Count;i++)
        {var tile=(FrameworkElement)_tiles.Children[i];tile.Margin=new Thickness(0,0,(i+1)%tileColumns==0?0:16,20);}
        _columns.ColumnDefinitions.Clear();_columns.RowDefinitions.Clear();if(_left==null||_right==null)return;
        if(available>=1050)
        {_columns.ColumnDefinitions.Add(new ColumnDefinition());_columns.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(340)});Grid.SetColumn(_left,0);Grid.SetRow(_left,0);Grid.SetColumn(_right,1);Grid.SetRow(_right,0);_left.Margin=new Thickness(0,0,20,0);}
        else
        {_columns.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});_columns.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Grid.SetColumn(_left,0);Grid.SetRow(_left,0);Grid.SetColumn(_right,0);Grid.SetRow(_right,1);_left.Margin=new Thickness(0);}
    }
}
