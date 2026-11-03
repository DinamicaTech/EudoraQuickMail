using System.IO;
using System.Text.Json;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class MultiActionRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EQM-multi-rule-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingMail _mail = new();
    private readonly StubLocalStoreService _store = new();
    private RuleService Service() => new(_mail, _store, _root);
    private static MailMessageSummary Message() => new() { AccountId = Guid.NewGuid(), MessageId = "42", FolderName = "In", From = "sender@example.com" };
    private static MailRule ForwardMove() => new() { Name = "Forward + move", FromContains = "sender@", TargetFolder = "Archive",
        AdditionalActions = [new() { Action = RuleAction.Forward, ForwardTo = "archive@example.net" }] };

    [Fact]
    public void LegacyRulesAndMultipleActionsRoundTripWithoutChangingFolderReferences()
    {
        var legacy = JsonSerializer.Deserialize<MailRule>("{\"Action\":2,\"TargetFolder\":\"Archive\",\"AlsoMarkAsRead\":true}")!;
        Assert.Single(legacy.GetActionSteps());
        var service = Service(); var rule = ForwardMove(); service.SaveRules([rule, legacy]);
        var loaded = Service().LoadRules();
        Assert.Equal(2, loaded[0].GetActionSteps().Count);
        Assert.Equal(RuleAction.MoveToFolder, loaded[0].Action);
        Assert.True(loaded[1].AlsoMarkAsRead);
        Assert.Equal(2, service.RewriteFolderTargets([new("Archive", "Records", false)]));
        Assert.All(Service().LoadRules(), r => Assert.Equal("Records", r.TargetFolder));
        Assert.Equal("archive@example.net", Service().LoadRules()[0].AdditionalActions[0].ForwardTo);
    }

    [Fact]
    public async Task ForwardMustFinishQueuingBeforeMarkReadAndMove()
    {
        var service = Service(); var rule = ForwardMove(); service.SaveRules([rule]);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ForwardMessageAsync = async (r, _, ct) =>
        { Assert.Equal(rule.Id, r.Id); _mail.Events.Add("forward"); await ready.Task.WaitAsync(ct); };
        var message = Message(); var incoming = new List<MailMessageSummary> { message };
        var run = service.ApplyRulesAsync(incoming, message.AccountId, CancellationToken.None);
        Assert.Equal(new[] { "forward" }, _mail.Events);
        ready.SetResult();
        var result = await run;
        Assert.Equal(new[] { "forward", "read", "move" }, _mail.Events);
        Assert.True(message.IsRead);
        Assert.Empty(incoming);
        Assert.Single(result.RemovedMessages);
    }

    [Fact]
    public async Task ForwardFailureDoesNotReadOrMoveOriginal()
    {
        var service = Service(); service.SaveRules([ForwardMove()]);
        service.ForwardMessageAsync = (_, _, _) => throw new IOException("queue unavailable");
        var message = Message(); var incoming = new List<MailMessageSummary> { message };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRulesAsync(incoming, message.AccountId, CancellationToken.None));
        Assert.Contains("Completed actions: none", error.Message);
        Assert.Empty(_mail.Events); Assert.Single(incoming); Assert.False(message.IsRead);
    }

    [Fact]
    public async Task CancellationDuringForwardDoesNotRunLaterActions()
    {
        var service = Service(); service.SaveRules([ForwardMove()]);
        using var cancellation = new CancellationTokenSource();
        service.ForwardMessageAsync = (_, _, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var message = Message();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyRulesAsync([message], message.AccountId, cancellation.Token));
        Assert.Empty(_mail.Events); Assert.False(message.IsRead);
    }

    [Fact]
    public async Task MissingFlagIsRejectedBeforeForwarding()
    {
        var service = Service(); service.FlagService = new StubFlagService();
        var rule = ForwardMove(); rule.AdditionalActions.Add(new() { Action = RuleAction.SetFlag, FlagId = Guid.NewGuid().ToString() });
        service.SaveRules([rule]);
        service.ForwardMessageAsync = (_, _, _) => throw new Exception("Must not forward");
        var message = Message();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None));
        Assert.Contains("flag no longer exists", error.Message);
        Assert.Empty(_mail.Events);
    }

    [Fact]
    public async Task MoveFailureReportsPreviouslyCompletedActions()
    {
        var service = Service(); service.SaveRules([ForwardMove()]); _mail.FailMove = true;
        service.ForwardMessageAsync = (_, _, _) => { _mail.Events.Add("forward"); return Task.CompletedTask; };
        var message = Message();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None));
        Assert.Contains("Completed actions: Forward to archive@example.net", error.Message);
        Assert.Contains("network down", error.Message);
        Assert.Equal(new[] { "forward", "read", "move" }, _mail.Events);
    }

    [Fact]
    public async Task ReadFailureStopsMoveAndReportsForwardCompleted()
    {
        var service = Service(); service.SaveRules([ForwardMove()]); _mail.FailRead = true;
        service.ForwardMessageAsync = (_, _, _) => { _mail.Events.Add("forward"); return Task.CompletedTask; };
        var message = Message();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None));
        Assert.Contains("failed at Also mark as read", error.Message);
        Assert.Equal(new[] { "forward", "read" }, _mail.Events);
    }

    [Fact]
    public async Task FlagsPersistAndCanBeRemovedWithoutMoving()
    {
        Directory.CreateDirectory(_root);
        var store = new LocalStoreService(new ProfileContext(_root)); store.Initialize();
        var service = new RuleService(_mail, store, _root) { FlagService = new StubFlagService() };
        var message = Message(); await store.UpsertSummariesAsync([message]);
        var rule = new MailRule { Name = "Flag", FromContains = "sender@", Action = RuleAction.SetFlag, FlagId = FlagDefinition.BuiltInFlagId.ToString() };
        service.SaveRules([rule]);
        await service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None);
        Assert.Equal(rule.FlagId, (await store.LoadAllSummariesAsync()).Single().FlagId);
        Assert.Equal("Flagged", message.FlagName);
        rule.FlagId = null;
        await service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None);
        Assert.Null(message.FlagId);
        Assert.Null((await store.LoadAllSummariesAsync()).Single().FlagId);
    }

    [Fact]
    public async Task FlagIsAssignedBeforeMove()
    {
        var service = Service(); service.FlagService = new StubFlagService();
        var rule = ForwardMove(); rule.AdditionalActions = [new() { Action = RuleAction.SetFlag, FlagId = FlagDefinition.BuiltInFlagId.ToString() }];
        service.SaveRules([rule]); var message = Message();
        _mail.BeforeMove = () => Assert.Equal(FlagDefinition.BuiltInFlagId.ToString(), message.FlagId);
        var result = await service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None);
        Assert.Single(result.RemovedMessages);
    }

    [Fact]
    public async Task InvalidCombinationIsRejectedBeforeAnySideEffect()
    {
        var service = Service(); var rule = ForwardMove();
        rule.AdditionalActions.Add(new() { Action = RuleAction.Delete }); service.SaveRules([rule]);
        service.ForwardMessageAsync = (_, _, _) => throw new Exception("Must not reach forwarding");
        var message = Message();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None));
        Assert.Contains("Only one destination", error.Message); Assert.Empty(_mail.Events);
    }

    [Fact]
    public void QuickMoveDoesNotOverwriteExistingMultiActionRule()
    {
        var service = Service(); var rule = ForwardMove(); service.SaveRules([rule]);
        var result = service.SaveOrUpdateQuickMoveRule(new() { FromContains = "sender@", TargetFolder = "Other" });
        Assert.True(result.Created); Assert.Equal("Archive", rule.TargetFolder);
        Assert.Equal(2, service.LoadRules().Count);
    }

    [Fact]
    public void PreviewListsAllActionsWithMoveLast()
    {
        var rule = ForwardMove(); rule.AdditionalActions.Add(new() { Action = RuleAction.SetFlag });
        var result = RulePreviewBuilder.Build(rule, [], [], "In");
        Assert.Contains("Remove flag", result.ActionDescription);
        Assert.True(result.ActionDescription.IndexOf("Queue forwarding", StringComparison.Ordinal) < result.ActionDescription.IndexOf("Move to Archive", StringComparison.Ordinal));
        Assert.Contains("Also mark as read", result.ActionDescription);
    }

    [Fact]
    public void EditorAddsRemovesActionsAndPreservesIndependentReadOption()
    {
        var stub = new StubRuleService { LoadedRules = [ForwardMove()] };
        var vm = new RulesManagerViewModel(stub, []);
        Assert.Equal(2, vm.Actions.Count);
        vm.AddActionCommand.Execute(null);
        Assert.Equal(RuleAction.SetFlag, vm.SelectedAction!.Action);
        Assert.True(vm.SelectedRule!.AlsoMarkAsRead);
        vm.SelectedAction.Action = RuleAction.Forward;
        vm.SelectedAction.ForwardTo = "second@example.net";
        Assert.Contains(vm.SelectedRule.GetActionSteps(), a => a.ForwardTo == "second@example.net");
        vm.RemoveActionCommand.Execute(null);
        Assert.Equal(2, vm.SelectedRule.GetActionSteps().Count);
        vm.SelectedAction = vm.Actions.Single(a => a.Action == RuleAction.MoveToFolder);
        vm.PickFolderRequested += (_, _) => "Records";
        vm.PickFolderCommand.Execute(null);
        Assert.Equal("Records", vm.SelectedRule.TargetFolder);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteRunsBeforeMoveAndFailureStopsRemainingActions(bool fail)
    {
        var service = Service(); var rule = ForwardMove();
        rule.AdditionalActions = [new() { Action = RuleAction.Execute, CommandLine = "trusted.exe fixed", AddSubjectToCommandLine = true }];
        service.SaveRules([rule]);
        service.ExecuteCommandAsync = (action, message, ct) =>
        {
            Assert.Equal("trusted.exe fixed", action.CommandLine);
            Assert.True(action.AddSubjectToCommandLine);
            _mail.Events.Add("execute");
            if (fail) throw new IOException("program failed");
            return Task.CompletedTask;
        };
        var message = Message();
        if (fail)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None));
            Assert.Contains("failed at Execute command", error.Message);
            Assert.Equal(new[] { "execute" }, _mail.Events);
        }
        else
        {
            await service.ApplyRulesAsync([message], message.AccountId, CancellationToken.None);
            Assert.Equal(new[] { "execute", "read", "move" }, _mail.Events);
        }
    }

    private sealed class RecordingMail : StubImapMailServiceBase, IMailService
    {
        public List<string> Events { get; } = [];
        public bool FailMove { get; set; }
        public bool FailRead { get; set; }
        public Action? BeforeMove { get; set; }
        public new Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> ids, CancellationToken ct = default)
        { Events.Add("read"); if (FailRead) throw new IOException("read failed"); return Task.CompletedTask; }
        public new Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> ids, string destinationFolder, CancellationToken ct = default)
        { BeforeMove?.Invoke(); Events.Add("move"); if (FailMove) throw new IOException("network down"); return Task.CompletedTask; }
    }
}

[Collection("WpfTests")]
public sealed class MultiActionRuleWindowTests : WpfTestBase
{
    [WpfFact]
    public async Task EachInlineActionShowsItsOwnParameters()
    {
        var vm = new RulesManagerViewModel(new StubRuleService
        { LoadedRules = [new() { Name = "Example", FromContains = "example", TargetFolder = "Archive" }] }, []);
        var window = new QuickMail.Views.RulesManagerWindow(vm, [], new Dictionary<Guid, List<MailFolderModel>>());
        try
        {
            foreach (var (action, panelName) in new[]
            {
                (RuleAction.MoveToFolder, "MoveActionPanel"), (RuleAction.Forward, "ForwardActionPanel"),
                (RuleAction.SetFlag, "FlagActionPanel"), (RuleAction.ReplyTemplate, "ReplyActionPanel"),
                (RuleAction.Autoresponse, "AutoresponseActionPanel"),
                (RuleAction.Execute, "ExecuteActionPanel"),
                (RuleAction.MarkAsRead, "")
            })
            {
                vm.SelectedAction!.Action = action;
                if (action == RuleAction.Autoresponse)
                {
                    Assert.Equal("[SUBJECT]", vm.SelectedAction.AutoResponseSubject);
                    Assert.Equal("[BODY]", vm.SelectedAction.AutoResponseBody);
                }
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var editors = (System.Windows.Controls.ItemsControl)window.FindName("ActionEditors");
                editors.ApplyTemplate();
                editors.Measure(new System.Windows.Size(450, double.PositiveInfinity));
                editors.Arrange(new System.Windows.Rect(0, 0, 450, editors.DesiredSize.Height)); editors.UpdateLayout();
                foreach (var name in new[] { "MoveActionPanel", "ForwardActionPanel", "FlagActionPanel", "ReplyActionPanel", "AutoresponseActionPanel", "ExecuteActionPanel" })
                    Assert.Equal(name == panelName ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed,
                        Descendants(editors).OfType<System.Windows.FrameworkElement>().Single(e => e.Name == name).Visibility);
                Assert.True(vm.SelectedRule!.AlsoMarkAsRead);
            }
        }
        finally { window.Close(); }
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [WpfFact]
    public async Task InlineDropdownsEditAndRemoveTheirOwnActionWithoutNavigation()
    {
        var vm = new RulesManagerViewModel(new StubRuleService
        { LoadedRules = [new() { Name = "Example", FromContains = "example", TargetFolder = "Archive" }] }, []);
        vm.AddActionCommand.Execute(null);
        var window = new QuickMail.Views.RulesManagerWindow(vm, [], new Dictionary<Guid, List<MailFolderModel>>());
        try
        {
            var editors = (System.Windows.Controls.ItemsControl)window.FindName("ActionEditors");
            editors.ApplyTemplate();
            editors.Measure(new System.Windows.Size(450, double.PositiveInfinity));
            editors.Arrange(new System.Windows.Rect(0, 0, 450, editors.DesiredSize.Height)); editors.UpdateLayout();
            var combos = Descendants(editors).OfType<System.Windows.Controls.ComboBox>().Where(c => c.Name == "ActionCombo").ToList();
            Assert.Equal(2, combos.Count);
            Assert.Equal(10, combos[0].Items.Count);
            combos[0].SelectedValue = RuleAction.Execute;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(RuleAction.Execute, vm.Actions[0].Action);
            Assert.Equal(RuleAction.MoveToFolder, vm.Actions[1].Action);
            combos[0].SelectedValue = (RuleAction)(-1);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Single(vm.Actions);
            Assert.Equal(RuleAction.MoveToFolder, vm.Actions[0].Action);
        }
        finally { window.Close(); }
    }
}
