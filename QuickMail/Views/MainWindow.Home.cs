using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DinamicaBackup;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "_homeLifetime is cancelled and disposed in the Closed handler registered by InitializeHome. WPF closes windows through Closed, not IDisposable; implementing IDisposable would not be called by WPF.")]
public partial class MainWindow
{
    private readonly HomeIdlePolicy _homeIdle = new();
    private readonly Stopwatch _homeClock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _homeLifetime = new();
    private DispatcherTimer? _homeTimer;
    private HomeDashboardView? _homeView;
    private HomeDashboardData? _homeData;
    private bool _homeActionRunning;
    private bool _homeLoading;
    private DateTime _homeUpdated;
    private Point? _homeMousePosition;
    internal void InitializeHome()
    {
        if (_homeTimer != null) return;
        _vm.EnableHomeTab();
        _homeView = new HomeDashboardView();
        _homeView.ActionRequested += HandleHomeAction;
        HomeHost.Content = _homeView;
        InputManager.Current.PreProcessInput += HomeInput;
        _homeIdle.Input(_homeClock.Elapsed);
        _homeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _homeTimer.Tick += HomeTick;
        _homeTimer.Start();
        Closed += (_, _) =>
        {
            _homeTimer.Stop();
            InputManager.Current.PreProcessInput -= HomeInput;
            _homeLifetime.Cancel();
            _homeLifetime.Dispose();
        };
    }

    private void HomeInput(object sender, PreProcessInputEventArgs e)
    {
        var input = e.StagingItem.Input;
        if (input is MouseEventArgs mouse &&
            (input.RoutedEvent == Mouse.MouseMoveEvent || input.RoutedEvent == Mouse.PreviewMouseMoveEvent))
        {
            var position = mouse.GetPosition(this);
            if (_homeMousePosition == position) return;
            _homeMousePosition = position;
            _homeIdle.Input(_homeClock.Elapsed);
        }
        else if (input is MouseButtonEventArgs or MouseWheelEventArgs or KeyEventArgs or TextCompositionEventArgs or StylusEventArgs)
            _homeIdle.Input(_homeClock.Elapsed);
    }

    private async void HomeTick(object? sender, EventArgs e)
    {
        var blocked = _homeActionRunning || !IsEnabled || _vm.IsBusy || _backupDraining || _updatePreparing || BackupOperationTracker.Busy
            || System.Windows.Interop.ComponentDispatcher.IsThreadModal
            || _vm.OpenTabs.Any(t => t is ComposeTabViewModel)
            || Application.Current.Windows.OfType<Window>().Any(w => w != this && w.IsVisible)
            || Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed;
        if (_homeIdle.ShouldShow(_homeClock.Elapsed, _configService.Load().ShowHomeAfterInactivity, blocked))
            _vm.ActiveTab = _vm.OpenTabs.OfType<HomeTabViewModel>().FirstOrDefault();
        if (!blocked && _vm.ActiveTab is HomeTabViewModel && DateTime.Now - _homeUpdated > TimeSpan.FromMinutes(5))
            await RefreshHomeAsync();
    }


    private async Task RefreshHomeAsync(bool force = false)
    {
        if (_homeView == null || _homeLoading || _homeLifetime.IsCancellationRequested) return;
        if (!force && DateTime.Now - _homeUpdated < TimeSpan.FromMinutes(5)) return;
        _homeLoading = true;
        _homeView.SetBusy(true,"Refreshing local summaries…");
        string? refreshError = null;
        try
        {
            var now=DateTime.Now;
            var accounts=_vm.Accounts.ToList();
            var notices=new List<HomeNotice>();
            IReadOnlyList<AccountHealthObservation> evidence;
            try { evidence=await Task.Run(()=>new AccountHealthJournal(_profileContext.ProfileDir).Read(),_homeLifetime.Token); }
            catch(Exception ex)
            {
                evidence=[];
                notices.Add(new("Health history could not be read. "+ex.Message,"Refresh","Retry reading local health records.",new("refresh"),true));
            }
            var queue=_outgoingQueue is ScheduledSendService scheduler ? await scheduler.GetSnapshotAsync() : Array.Empty<ScheduledMail>();
            var rows=accounts.Select(a=>
            {
                var receive=evidence.FirstOrDefault(x=>x.AccountId==a.Id&&x.Channel=="Receive");
                var send=evidence.FirstOrDefault(x=>x.AccountId==a.Id&&x.Channel=="Send");
                var network=a.IsActive&&!a.IsShared&&a.BackendKind!=BackendKind.LocalArchive;
                (string Label,string Color) State(AccountHealthObservation? observation,bool enabled) => !enabled ? ("Disabled / local",HomeDashboardData.Gray)
                    : observation==null ? ("Not checked yet",HomeDashboardData.Amber)
                    : observation.Error!=null ? ("Failed",HomeDashboardData.Red)
                    : now-observation.AttemptUtc.LocalDateTime>TimeSpan.FromDays(1) ? ("Last OK >24 h",HomeDashboardData.Amber)
                    : ("Last check OK",HomeDashboardData.Green);
                var rx=State(receive,network&&a.CheckIncomingMail);var tx=State(send,network);
                var detail=string.Join("\n",new[]{receive?.Error,send?.Error,
                    receive==null?null:$"Receive last attempt: {receive.AttemptUtc.ToLocalTime():g}",
                    send==null?null:$"Send last attempt: {send.AttemptUtc.ToLocalTime():g}"}.Where(x=>x!=null));
                if(network&&(receive?.Error!=null||send?.Error!=null))
                    notices.Add(new($"{a.AccountLabel}: {(receive?.Error!=null?"receiving":"sending")} failed. {detail}","Test","Test this account without sending an email.",new("test",a.Id),true));
                return new HomeAccountStatus(a.Id,a.AccountLabel,network,rx.Label,tx.Label,rx.Color,tx.Color,
                    receive?.SuccessUtc?.ToLocalTime().ToString("g")??"—",detail,rx.Color==HomeDashboardData.Green,
                    network&&a.CheckIncomingMail&&receive==null,network&&send==null);
            }).ToList();
            var untested=rows.Count(a=>a.SendUnknown);
            if(untested>0)notices.Add(new($"Sending has not been verified on {untested} account(s) in this version. Stored settings alone do not prove that sending works.",
                "Test sending on all accounts","Connect and authenticate without sending a message.",new("test-sending")));
            var unknown=rows.Where(a=>a.ReceiveUnknown).ToList();
            if(unknown.Count>0)notices.Add(new($"{unknown.Count} account(s) have no successful receive check recorded in this version: {string.Join(", ",unknown.Select(a=>a.Name))}.",
                "Check these accounts now","Download incoming mail only for these accounts. Does not dispatch Scheduled mail.",new("receive-unknown")));
            var backup=await Task.Run(ReadHomeBackupData,_homeLifetime.Token);
            notices.InsertRange(0,backup.Notices);
            var errors=queue.Count(q=>q.LastError!=null);
            foreach(var group in queue.Where(q=>q.LastError!=null).GroupBy(q=>q.Message.AccountId))
                notices.Add(new($"{accounts.FirstOrDefault(a=>a.Id==group.Key)?.AccountLabel}: {group.Count()} outgoing message(s) have errors.","Review Scheduled","Open this account's Scheduled folder.",new("scheduled",group.Key),true));
            var calendars=accounts.Where(a=>a.AuthType==AuthType.OAuth2Google ||
                string.Equals(a.CalendarProvider,"google",StringComparison.OrdinalIgnoreCase))
                .Select(a=>HomeCalendarStatus.From(a,evidence.FirstOrDefault(h=>h.AccountId==a.Id&&h.Channel=="Calendar"),DateTimeOffset.Now)).ToList();
            string? agendaWarning=null;
            try
            {
                if(_vm.CalendarVm!=null) await _vm.CalendarVm.ReloadFromStoreAsync(_homeLifetime.Token);
                if(_vm.CalendarVm==null || _vm.CalendarVm.IsUnavailable) agendaWarning="Calendar is unavailable. No current agenda could be loaded.";
            }
            catch(Exception ex) when(!_homeLifetime.IsCancellationRequested)
            { agendaWarning="Could not reload the agenda. Previously loaded events may be out of date.";LogService.Log("Home agenda",ex); }
            if(calendars.Any(c=>c.Enabled&&c.Color!=HomeDashboardData.Green))
                agendaWarning ??= "Showing cached events. Some calendars are unverified or out of date; an empty agenda does not confirm there are no appointments.";
            var appointments=_vm.CalendarVm?.TodayEvents.Select(e=>new HomeAppointment(e.IsAllDay?"All day":
                (e.StartTime?.ToString("t")??"")+(e.EndTime.HasValue?"–"+e.EndTime.Value.ToString("t"):""),
                e.Summary,QuickMail.Helpers.CalendarAccountColors.For(e.AccountId),
                (accounts.FirstOrDefault(a=>a.Id==e.AccountId)?.AccountLabel??"Local calendar")+
                (string.IsNullOrWhiteSpace(e.CalendarName)?"":" · "+e.CalendarName),e)).ToList()??[];
            _homeData=new(now,_vm.LastSyncText,rows,notices,backup,queue.Count(q=>q.SendAtUtc>DateTimeOffset.UtcNow),
                queue.Count(q=>q.SendAtUtc<=DateTimeOffset.UtcNow),errors,queue.Count(q=>q.Message.FollowUp!=null),
                _homeData?.Updated.Date==now.Date?_homeData.Statistics:null,null,appointments)
                { Calendars=calendars, AgendaWarning=agendaWarning };
            _homeView.Render(_homeData);
            if(_localStore is LocalStoreService store)
            {
                try
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_homeLifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    _homeData=_homeData with {Statistics=await store.ReadHomeStatisticsAsync(now.Date,timeout.Token),StatisticsError=null};
                }
                catch(Exception ex) when(!_homeLifetime.IsCancellationRequested)
                {
                    _homeData=_homeData with {Statistics=null,StatisticsError="Statistics unavailable or time limit reached. Refresh to retry."};
                    LogService.Log("Home statistics",ex);
                }
            }
            _homeUpdated=now;_homeView.Render(_homeData);
        }
        catch(OperationCanceledException) { }
        catch(Exception ex)
        { refreshError="Could not refresh Home: "+ex.Message;LogService.Log("Home refresh",ex); }
        finally
        {
            _homeLoading=false;
            if(!_homeLifetime.IsCancellationRequested)
                _homeView.SetBusy(_homeActionRunning,refreshError??$"Updated {_homeUpdated:t} · last download {_vm.LastSyncText} · refreshes every 5 min");
        }
    }

    private async void HandleHomeAction(HomeAction action)
    {
        if(_homeActionRunning||_homeLifetime.IsCancellationRequested)return;
        try
        {
            switch(action.Kind)
            {
                case "refresh": await RefreshHomeAsync(true);break;
                case "accounts": OpenAccountManager();await RefreshHomeAsync(true);break;
                case "account":
                    if(_vm.Accounts.FirstOrDefault(a=>a.Id==action.AccountId) is {} account) OpenAccountManagerForAccount(account);
                    await RefreshHomeAsync(true);break;
                case "backup": OpenBackupConfiguration(action.Value);break;
                case "appointment": _vm.CalendarVm?.NewEventAt(DateTime.Now);break;
                case "open-appointment":
                    if(action.Event!=null && _vm.CalendarVm!=null)
                    { _vm.CalendarVm.SelectedEvent=action.Event;ActivateSelectedCalendarEvent(); }
                    break;
                case "calendar-sync": await RetryHomeCalendarAsync(action.AccountId);break;
                case "search": await _vm.SearchFromHomeAsync(action.Value??"");break;
                case "unread": await _vm.SearchUnreadFromHomeAsync();break;
                case "folder":
                case "drafts":
                    if(action.AccountId is {} folderAccount && action.Value is {} folder)
                        await _vm.OpenHomeFolderAsync(folderAccount,folder,action.Kind=="folder");
                    break;
                case "scheduled": await _vm.OpenHomeScheduledAsync(action.AccountId);break;
                case "test":
                case "test-all":
                case "test-sending":
                case "receive-unknown": await RunHomeAccountActionAsync(action);break;
            }
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Home",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }

    private async Task RetryHomeCalendarAsync(Guid? accountId)
    {
        var account=_vm.Accounts.FirstOrDefault(a=>a.Id==accountId);
        if(account==null || !account.SyncCalendar || _graphCalendarSyncService==null)return;
        if(_vm.IsBusy||_backupDraining||_updatePreparing||BackupOperationTracker.Busy ||
            Application.Current is App { MailActivityPolicy: { Mode: not MailActivityMode.Online } })
        { MessageBox.Show(this,"Calendar sync is unavailable during another operation or Offline/Focus mode.","Google Calendar");return; }
        _homeActionRunning=true;_homeView?.SetBusy(true,"Synchronizing Google Calendar…");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_homeLifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        var indicator=new SlowOperationWindow("Google Calendar",()=>"Synchronizing "+account.AccountLabel,()=>cancellation.Cancel()){Owner=this};
        indicator.Closed+=(_,_)=>cancellation.Cancel();
        indicator.Show();
        try { await _graphCalendarSyncService.SyncAccountCalendarAsync(account,cancellation.Token); }
        catch(OperationCanceledException)
        { if(!_homeLifetime.IsCancellationRequested)MessageBox.Show(this,"Synchronization cancelled or timed out. Cached events are still available.","Google Calendar"); }
        finally { indicator.Close();_homeActionRunning=false;await RefreshHomeAsync(true); }
    }

    private async Task RunHomeAccountActionAsync(HomeAction action)
    {
        if(_vm.IsBusy||_backupDraining||_updatePreparing||BackupOperationTracker.Busy)
        {MessageBox.Show(this,"Another mail operation is running. Please try again when it finishes.","Home");return;}
        var eligible=_vm.Accounts.Where(a=>a.IsActive&&!a.IsShared&&a.BackendKind!=BackendKind.LocalArchive).ToList();
        if(action.AccountId.HasValue)eligible=eligible.Where(a=>a.Id==action.AccountId).ToList();
        if(action.Kind=="receive-unknown")eligible=eligible.Where(a=>a.CheckIncomingMail&&
            _homeData?.Accounts.Any(r=>r.Id==a.Id&&r.ReceiveUnknown)==true).ToList();
        if(eligible.Count==0)return;
        _homeActionRunning=true;_homeView?.SetBusy(true,"Testing accounts…");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_homeLifetime.Token);
        var stage="Preparing account checks…";
        var report=new List<string>();
        var indicator=new SlowOperationWindow("Home — account checks",()=>stage,()=>cancellation.Cancel()){Owner=this};
        indicator.Closed+=(_,_)=>cancellation.Cancel();
        indicator.Show();
        try
        {
            if(action.Kind=="receive-unknown")
            {
                if(Application.Current is App app)
                {
                    app.MailActivityPolicy?.GoOnline();
                    var ids=eligible.Select(a=>a.Id).ToArray();
                    var failures=await _vm.CheckRemoteMailNowAsync(s=>stage=s,ids,cancellation.Token);
                    report.AddRange(failures.Select(f=>MailOperationError.Describe(f.Operation,f.Account,f.Error)));
                    report.AddRange(await app.CheckHomePop3AccountsAsync(ids,s=>stage=s,cancellation.Token));
                    report.Add("Selected incoming accounts checked. No Scheduled messages were dispatched.");
                }
            }
            else
            {
                foreach(var account in eligible)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    stage=$"{account.AccountLabel}: preparing test…";
                    if(action.Kind=="test-sending")
                    {
                        if(account.BackendKind==BackendKind.MicrosoftGraph)
                        { report.Add(account.AccountLabel+": Graph has no SMTP probe. Only a real send verifies sending permission.");continue; }
                        string? password=account.BackendKind==BackendKind.Pop3Smtp
                            ? new DpapiAccountSecretProtector().GetSmtpPassword(account):_credentials.GetPassword(account.Id);
                        var watch=Stopwatch.StartNew();
                        try
                        {
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(30));
                            using var progress=ConnectionTestTrace.Begin(s=>stage=account.AccountLabel+": "+s);
                            if(account.AuthType is AuthType.OAuth2Google or AuthType.OAuth2Microsoft)
                                await _oauth.EnsureSilentTokenAsync(account,timeout.Token);
                            await _smtp.VerifyAsync(account,password,timeout.Token);
                            AccountHealthJournal.Observe(account.Id,"Send");report.Add(account.AccountLabel+": sending connection OK (no email sent).");
                        }
                        catch(Exception ex) when(!cancellation.IsCancellationRequested)
                        {
                            AccountHealthJournal.Observe(account.Id,"Send",ex is OperationCanceledException?new TimeoutException():ex);
                            report.Add(account.AccountLabel+": "+ConnectionTestTrace.Describe(ex,stage,watch.Elapsed,password));
                        }
                    }
                    else
                    {
                        var editor=new AccountManagerViewModel(_accountService,_credentials,_imap,_oauth,_localStore,_configService,_featureGate,_providerCatalog,_autoDiscover,_smtp,_contactSyncService,_graphCalendarSyncService);
                        editor.SelectedAccount=editor.Accounts.FirstOrDefault(a=>a.Id==account.Id);
                        if(editor.SelectedAccount==null)continue;
                        editor.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(editor.ConnectionTestStage))stage=account.AccountLabel+": "+editor.ConnectionTestStage;};
                        editor.ConnectionTestCompleted+=(message,success)=>report.Add(account.AccountLabel+"\n"+message);
                        var test=editor.TestConnectionCommand.ExecuteAsync(null);
                        using var cancel=cancellation.Token.Register(editor.CancelConnectionTest);
                        await test;
                    }
                }
            }
        }
        catch(OperationCanceledException){report.Add("Cancelled. Checks already completed have been kept.");}
        finally { indicator.Close();_homeActionRunning=false;await RefreshHomeAsync(true); }
        MessageBox.Show(this,string.Join("\n\n",report),"Account check results",MessageBoxButton.OK,MessageBoxImage.Information);
    }

    private HomeBackupData ReadHomeBackupData()
    {
        var empty=new HomeBackupStatus("Not recorded",HomeDashboardData.Gray,"—","");
        try
        {
            var path=Path.Combine(_profileContext.ProfileDir,"backup-job.json");
            if(!File.Exists(path))return new(false,empty,empty,"Not configured",[]);
            var job=JsonFiles.Read<BackupJob>(path);job.ProfileDirectory=_profileContext.ProfileDir;
            var runs=Catalog.ReadRecentReceipts(job,100,includeDestination:false);
            var last=runs.FirstOrDefault(r=>r.Kind=="Backup");var restore=runs.FirstOrDefault(r=>r.Kind=="Test");
            HomeBackupStatus Status(RunResult? run)=>run==null?empty:new(run.Status,
                run.Status=="Success"?HomeDashboardData.Green:run.Status=="Failed"?HomeDashboardData.Red:HomeDashboardData.Amber,
                $"{run.StartedUtc.ToLocalTime():g} · took {ReportFormatting.Duration(run.FinishedUtc-run.StartedUtc)}",
                string.Join("\n",new[]{run.Detail}.Concat(run.Warnings).Where(x=>!string.IsNullOrWhiteSpace(x))));
            var notices=new List<HomeNotice>();
            if(last is {Status:"Failed" or "Warning"})notices.Add(new("Latest backup "+last.Status.ToLowerInvariant()+": "+last.Detail,"Backup settings…","Review backup settings and the error.",new("backup",Value:"Native Backup"),true));
            var missingFolder=job.TestSchedule.Enabled&&string.IsNullOrWhiteSpace(job.RestoreTestDirectory);
            if(restore is {Status:"Failed" or "Warning"}||missingFolder)
                notices.Add(new(missingFolder?"Restore test cannot run: no restore-test folder has been chosen.":"Restore test "+restore!.Status.ToLowerInvariant()+": "+restore.Detail,
                    missingFolder?"Choose restore-test folder…":"Review restore test…","Open the Restore test settings tab.",new("backup",Value:"Restore test"),true));
            var due=job.BackupSchedule.LatestOccurrence(DateTime.Now);
            if(due.HasValue&&(last==null||last.StartedUtc.LocalDateTime<due.Value.AddMinutes(-5)))
                notices.Add(new("No local receipt for the latest scheduled backup.","History…","Review the backup history.",new("backup",Value:"History"),true));
            DateTime? next=null;
            if(job.BackupSchedule.Enabled)for(var d=0;d<63;d++)
            {
                var candidate=job.BackupSchedule.LatestOccurrence(DateTime.Today.AddDays(d+1).AddTicks(-1));
                if(candidate>DateTime.Now){next=candidate;break;}
            }
            return new(job.BackupSchedule.Enabled,Status(last),
                missingFolder?new("Needs setup",HomeDashboardData.Red,restore==null?"—":Status(restore).When,"No restore-test folder chosen"):Status(restore),
                next?.ToString("g")??"Not scheduled",notices);
        }
        catch(Exception ex){return new(false,empty,empty,"Unknown",[new("Local backup history could not be read: "+ex.Message,"History…","Open backup history.",new("backup",Value:"History"),true)]);}
    }
}
