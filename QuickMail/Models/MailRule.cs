using System;
using System.Text.Json.Serialization;

namespace QuickMail.Models;

/// <summary>
/// A user-defined rule that automatically performs an action on incoming messages
/// that match a set of conditions. All populated conditions are ANDed together.
/// An empty/null condition matches everything.
/// </summary>
public class MailRule
{
    public bool IsSpamFilter { get; set; }
    public bool SpamBlockDomain { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-visible name for this rule. Required — validated on save.</summary>
    public string Name { get; set; } = string.Empty;

    // A screen reader reads a data-bound Selector item's UIA Name from ToString()
    // (DisplayMemberPath only sets the visual). Without this the Rules list announces
    // "QuickMail.Models.MailRule" for every row. See CLAUDE.md.
    // The Rules list itself binds AutomationProperties.Name to AccessibleName below (so
    // it can include the account and honour the field-labels setting); ToString() remains
    // for any other Selector that binds MailRule without a template.
    public override string ToString() => Name;

    /// <summary>
    /// Display-only, resolved by <c>RulesManagerViewModel</c> — never serialized. The account
    /// scope shown in the list row: an account's label, or "All accounts" for an unscoped rule.
    /// </summary>
    [JsonIgnore]
    public string AccountDisplay { get; set; } = string.Empty;

    /// <summary>
    /// Display-only, resolved by <c>RulesManagerViewModel</c> — never serialized. The composed
    /// row accessible name (rule + account), labeled or concise per the <c>RuleListShowFieldLabels</c>
    /// setting, surfaced via <c>AutomationProperties.Name</c> on the list row.
    /// </summary>
    [JsonIgnore]
    public string AccessibleName { get; set; } = string.Empty;

    /// <summary>When false, the rule is skipped during evaluation.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Run automatically for newly-arrived Inbox mail; manual application ignores this flag.</summary>
    public bool ApplyAutomatically { get; set; } = true;

    /// <summary>
    /// When this rule is invoked manually for a folder, also evaluate the physical Out/Sent
    /// mailbox. This never makes a manual rule automatic and is deliberately ignored by the
    /// incoming-mail pipeline.
    /// </summary>
    public bool AlsoFilterOutMailbox { get; set; }

    // ── Conditions (all ANDed) ──────────────────────────────────────────────

    /// <summary>When true, the FromContains condition is active.</summary>
    public bool UseFromCondition { get; set; } = true;

    /// <summary>Case-insensitive substring match against MailMessageSummary.From.</summary>
    public string? FromContains { get; set; }

    /// <summary>When true, the ToContains condition is active.</summary>
    public bool UseToCondition { get; set; }

    /// <summary>Case-insensitive substring match against MailMessageSummary.To.</summary>
    public string? ToContains { get; set; }

    /// <summary>When true, the active To condition also searches CC and BCC recipients.</summary>
    public bool AlsoCcBcc { get; set; }

    /// <summary>When true, the SubjectContains condition is active.</summary>
    public bool UseSubjectCondition { get; set; }

    /// <summary>Case-insensitive substring match against MailMessageSummary.Subject.</summary>
    public string? SubjectContains { get; set; }

    /// <summary>When true, the BodyContains condition is active.</summary>
    public bool UseBodyCondition { get; set; }

    /// <summary>Case-insensitive substring match against MailMessageSummary.Preview.</summary>
    public string? BodyContains { get; set; }

    /// <summary>When true, only messages with HasAttachments == true match.</summary>
    public bool MustHaveAttachments { get; set; }

    /// <summary>
    /// Scope the rule to one account. Null means the rule applies to all accounts.
    /// </summary>
    public Guid? AccountId { get; set; }

    // ── Action ──────────────────────────────────────────────────────────────

    public RuleAction Action { get; set; } = RuleAction.MoveToFolder;

    /// <summary>Additionally marks a matched message read before another primary action.</summary>
    public bool AlsoMarkAsRead { get; set; } = true;

    /// <summary>
    /// Destination folder full name (e.g. "INBOX/Priority"). Required when
    /// Action == MoveToFolder; ignored otherwise.
    /// </summary>
    public string? TargetFolder { get; set; }
    public string? ForwardTo { get; set; }
    public int? ReplyTemplateId { get; set; }
    public string? AutoResponseSubject { get; set; }
    public string? AutoResponseBody { get; set; }
    public string? FlagId { get; set; }
    public string? CommandLine { get; set; }
    public bool AddSubjectToCommandLine { get; set; }
    /// <summary>Optional extra actions. Legacy rules keep their original single-action fields.</summary>
    public List<RuleActionStep> AdditionalActions { get; set; } = [];

    public List<RuleActionStep> GetActionSteps() =>
        new[] { new RuleActionStep { Action = Action, TargetFolder = TargetFolder, ForwardTo = ForwardTo,
            ReplyTemplateId = ReplyTemplateId,
            AutoResponseSubject = AutoResponseSubject ?? (Action == RuleAction.Autoresponse ? "[SUBJECT]" : null),
            AutoResponseBody = AutoResponseBody ?? (Action == RuleAction.Autoresponse ? "[BODY]" : null),
            FlagId = FlagId, CommandLine = CommandLine,
            AddSubjectToCommandLine = AddSubjectToCommandLine } }
        .Concat(AdditionalActions).OrderBy(a => a.IsDestination).ToList();

    public void SetActionSteps(IEnumerable<RuleActionStep> steps)
    {
        var all = steps.ToList();
        if (all.Count == 0) throw new ArgumentException("At least one action is required.");
        // Keep the single destination in the legacy fields: folder rename/cleanup and destination
        // tabs can continue using the same stable folder reference.
        var primary = all.FirstOrDefault(a => a.IsDestination) ?? all[0];
        Action = primary.Action; TargetFolder = primary.TargetFolder; ForwardTo = primary.ForwardTo;
        ReplyTemplateId = primary.ReplyTemplateId; FlagId = primary.FlagId;
        AutoResponseSubject = primary.AutoResponseSubject; AutoResponseBody = primary.AutoResponseBody;
        CommandLine = primary.CommandLine; AddSubjectToCommandLine = primary.AddSubjectToCommandLine;
        AdditionalActions = all.Where(a => !ReferenceEquals(a, primary)).Select(a => a.Copy()).ToList();
    }

    public MailRule ForAction(RuleActionStep step)
    {
        var copy = (MailRule)MemberwiseClone();
        copy.AdditionalActions = [];
        copy.Action = step.Action; copy.TargetFolder = step.TargetFolder; copy.ForwardTo = step.ForwardTo;
        copy.ReplyTemplateId = step.ReplyTemplateId; copy.FlagId = step.FlagId; copy.AlsoMarkAsRead = false;
        copy.AutoResponseSubject = step.AutoResponseSubject; copy.AutoResponseBody = step.AutoResponseBody;
        copy.CommandLine = step.CommandLine; copy.AddSubjectToCommandLine = step.AddSubjectToCommandLine;
        return copy;
    }
    /// <summary>Inclusive local date. Null means no expiration.</summary>
    public DateTime? ActiveUntil { get; set; }
    [JsonIgnore]
    public bool IsExpired => ActiveUntil.HasValue && ActiveUntil.Value.Date < DateTime.Today;
}

public enum RuleAction
{
    /// <summary>Mark the message as read (IMAP \Seen flag).</summary>
    MarkAsRead,

    /// <summary>Mark the message as unread (remove IMAP \Seen flag).</summary>
    MarkAsUnread,

    /// <summary>Move the message to TargetFolder via IMAP MOVE.</summary>
    MoveToFolder,

    /// <summary>Move the message to the Trash folder.</summary>
    Delete,
    Forward,
    ReplyTemplate,
    SetFlag,
    Execute,
    Autoresponse,
}

public partial class RuleActionStep : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private RuleAction _action = RuleAction.MoveToFolder;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _targetFolder;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _forwardTo;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int? _replyTemplateId;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _autoResponseSubject;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _autoResponseBody;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _flagId;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _commandLine;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _addSubjectToCommandLine;
    [JsonIgnore] public bool IsDestination => Action is RuleAction.MoveToFolder or RuleAction.Delete;
    [JsonIgnore] public string ForwardRecipientWarning => ForwardTo != null &&
        MimeKit.InternetAddressList.TryParse(ForwardTo.Replace(';', ','), out var addresses) && addresses.Mailboxes.Count() > 1
        ? "Each Forward action accepts one email address. Create two Forward actions, each with a different recipient, to forward to two addresses." : "";
    [JsonIgnore] public string Description => Action switch
    {
        RuleAction.MoveToFolder => $"Move to {TargetFolder ?? "(choose folder)"}",
        RuleAction.Delete => "Move to Trash",
        RuleAction.Forward => $"Forward to {ForwardTo ?? "(recipient)"}",
        RuleAction.ReplyTemplate => $"Reply template {ReplyTemplateId}",
        RuleAction.Autoresponse => "Autoresponse",
        RuleAction.SetFlag => FlagId == null ? "Remove flag" : "Set flag",
        RuleAction.MarkAsRead => "Mark as read",
        RuleAction.MarkAsUnread => "Mark as unread",
        RuleAction.Execute => "Execute command",
        _ => Action.ToString()
    };
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ForwardTo)) base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ForwardRecipientWarning)));
        if (e.PropertyName != nameof(Description)) base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Description)));
    }
    public RuleActionStep Copy() => new() { Action = Action, TargetFolder = TargetFolder,
        ForwardTo = ForwardTo, ReplyTemplateId = ReplyTemplateId, FlagId = FlagId,
        AutoResponseSubject = AutoResponseSubject ?? (Action == RuleAction.Autoresponse ? "[SUBJECT]" : null),
        AutoResponseBody = AutoResponseBody ?? (Action == RuleAction.Autoresponse ? "[BODY]" : null),
        CommandLine = CommandLine, AddSubjectToCommandLine = AddSubjectToCommandLine };
}
