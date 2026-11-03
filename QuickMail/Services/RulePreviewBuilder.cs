using QuickMail.Models;

namespace QuickMail.Services;

public static class RulePreviewBuilder
{
    public static RulePreviewResult Build(MailRule rule, IReadOnlyList<MailMessageSummary> messages,
        IReadOnlyList<MailMessageSummary> matches, string scope)
    {
        var steps = rule.GetActionSteps();
        var action = string.Join(" → ", steps.Select(step => step.Action switch
        {
            RuleAction.MoveToFolder => $"Move to {step.TargetFolder ?? "(no folder)"}.",
            RuleAction.Delete => "Move to Trash.",
            RuleAction.MarkAsRead => "Mark as read.",
            RuleAction.MarkAsUnread => "Mark as unread.",
            RuleAction.Forward => $"Queue forwarding to {step.ForwardTo}.",
            RuleAction.ReplyTemplate => "Queue template replies to eligible incoming senders.",
            RuleAction.Autoresponse => "Queue an autoresponse to each eligible incoming message.",
            RuleAction.SetFlag => step.FlagId == null ? "Remove flag." : $"Set flag {step.FlagId}.",
            RuleAction.Execute => $"Execute {step.CommandLine}" + (step.AddSubjectToCommandLine ? " (subject as last argument)." : "."),
            _ => "No action."
        }));
        if (rule.AlsoMarkAsRead && rule.Action != RuleAction.MarkAsRead) action += " Also mark as read.";
        if (steps.Any(s => s.Action is RuleAction.Forward or RuleAction.ReplyTemplate or RuleAction.Autoresponse))
            action += " Counts are condition matches, not guaranteed sends: deduplication and loop/bulk protections are checked at execution.";
        if (!rule.IsEnabled) action = "Rule is disabled; batch execution skips it. Preview of conditions only. " + action;
        if (rule.IsExpired) action = $"Expired on {rule.ActiveUntil:d}. No messages will be processed.";
        else if (rule.ActiveUntil.HasValue) action += $" Active through {rule.ActiveUntil:d} inclusive.";
        return new(rule.Name, scope, action, messages.Count, matches.Count,
            matches.Count(m => m.Direction == MessageDirection.Incoming), matches.Count(m => m.Direction == MessageDirection.Outgoing),
            rule.AlsoMarkAsRead || steps.Any(s => s.Action == RuleAction.MarkAsRead) ? matches.Count : 0,
            matches.OrderByDescending(m => m.Date).Take(20).ToList());
    }
}
