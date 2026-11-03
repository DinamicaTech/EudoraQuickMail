using System.IO;
using System.Text.Json;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class RuleExecuteTests
{
    [Theory]
    [InlineData("hello world")]
    [InlineData("\"; Remove-Item C:\\anything; # $(exit) & | > %PATH%")]
    [InlineData("line1\r\nline2\\\"")]
    public void SubjectIsOneLiteralArgument(string subject)
    {
        var start = RuleExecuteService.CreateStartInfo("powershell.exe -NoProfile -File \"C:\\My Scripts\\lights.ps1\" fixed", true, subject);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "-NoProfile", "-File", "C:\\My Scripts\\lights.ps1", "fixed", subject }, start.ArgumentList);
    }

    [Theory]
    [InlineData("cmd.exe /c echo hello")]
    [InlineData("powershell.exe -Command echo hello")]
    [InlineData("pwsh.exe -EncodedCommand abc")]
    [InlineData("powershell.exe -c echo -File x.ps1")]
    [InlineData("script.bat")]
    [InlineData("")]
    public void UnsafeSubjectInterpreterOrInvalidCommandIsRejected(string command) =>
        Assert.Throws<ArgumentException>(() => RuleExecuteService.CreateStartInfo(command, true, "subject"));

    [Fact]
    public void QuotedExecutableAndFixedArgumentsArePreserved()
    {
        var start = RuleExecuteService.CreateStartInfo("\"C:\\Program Files\\App\\app.exe\" \"two words\"", false, "ignored");
        Assert.Equal("C:\\Program Files\\App\\app.exe", start.FileName);
        Assert.Equal(new[] { "two words" }, start.ArgumentList);
    }

    [Fact]
    public async Task RealProcessExitCodesAreCheckedAndCancellationPreventsLaunch()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var rule = new MailRule { CommandLine = $"\"{exe}\" /d /c exit 0" };
        await RuleExecuteService.ExecuteAsync(rule, new(), TestContext.Current.CancellationToken);
        rule.CommandLine = $"\"{exe}\" /d /c exit 7";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RuleExecuteService.ExecuteAsync(rule, new(), TestContext.Current.CancellationToken));
        Assert.Contains("exit code 7", error.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuleExecuteService.ExecuteAsync(rule, new(), new CancellationToken(true)));
    }

    [Fact]
    public void ExecuteSurvivesJsonNormalizationAndPreviewNeverExecutesIt()
    {
        var rule = new MailRule();
        rule.SetActionSteps([new() { Action = RuleAction.Execute, CommandLine = "lights.exe on", AddSubjectToCommandLine = true },
            new() { Action = RuleAction.MoveToFolder, TargetFolder = "Processed" }]);
        var restored = JsonSerializer.Deserialize<MailRule>(JsonSerializer.Serialize(rule))!;
        var step = restored.GetActionSteps()[0];
        Assert.Equal("lights.exe on", restored.ForAction(step).CommandLine);
        Assert.True(restored.ForAction(step).AddSubjectToCommandLine);
        RuleService.ValidateActionSteps(restored);
        Assert.Contains("Execute lights.exe on", RulePreviewBuilder.Build(restored, [], [], "In").ActionDescription);
    }

    [Fact]
    public void EditorSortsDestinationsAndRemovesViaLastOption()
    {
        var vm = new RulesManagerViewModel(new StubRuleService { LoadedRules = [new() { Name = "Test", FromContains = "test", TargetFolder = "Archive" }] }, []);
        var move = vm.Actions[0];
        vm.AddActionCommand.Execute(null);
        Assert.Same(move, vm.Actions[^1]);
        move.Action = RuleAction.Forward;
        var flag = vm.Actions[0]; flag.Action = RuleAction.MoveToFolder;
        Assert.Same(flag, vm.Actions[^1]);
        move.Action = RulesManagerViewModel.ActionOptions[^1].Action;
        Assert.Single(vm.Actions);
        flag.Action = RulesManagerViewModel.ActionOptions[^1].Action;
        Assert.Empty(vm.Actions);
        Assert.Equal(RuleAction.MarkAsRead, vm.SelectedRule!.Action);
        Assert.True(vm.SelectedRule.AlsoMarkAsRead);
        vm.SelectedRule.AlsoMarkAsRead = false;
        vm.SaveRuleCommand.Execute(null);
        Assert.Contains("Add an action", vm.FolderError);
    }

    [Fact]
    public void MultipleForwardRecipientsExplainSeparateActions()
    {
        var step = new RuleActionStep { Action = RuleAction.Forward, ForwardTo = "a@example.com; b@example.com" };
        Assert.Contains("two Forward actions", step.ForwardRecipientWarning);
        var error = Assert.Throws<ArgumentException>(() => RuleForwardService.ValidateDestination(step.ForwardTo));
        Assert.Contains("two Forward actions", error.Message);
        step.ForwardTo = "\"Smith, John\" <john@example.com>";
        Assert.Empty(step.ForwardRecipientWarning);
        Assert.Equal("john@example.com", RuleForwardService.ValidateDestination(step.ForwardTo));
    }
}
