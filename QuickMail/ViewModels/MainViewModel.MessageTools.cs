using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel
{
    public AttachmentBrowserViewModel CreateAttachmentBrowser(MailMessageSummary? message = null)
    {
        var folder = SelectedFolder;
        return new(_localStore as LocalStoreService, _imap,
            message != null ? () => Task.FromResult(new List<MailMessageSummary> { message })
                : () => LoadCurrentFolderSummariesForRulesAsync(folder, preservePhysicalCopies: true),
            message != null ? message.Subject : folder?.DisplayName ?? "No folder selected");
    }

    public Task<string> AnalyzeRuleConflictsAsync(IReadOnlyList<MailMessageSummary> messages, CancellationToken ct)
        => _ruleService is RuleService rules ? Task.Run(() => rules.AnalyzeConflictsAsync(messages, ct), ct)
            : Task.FromResult("Rule conflict analysis is not available with this rule provider.");

    public Task<ConversationExportResult> ExportConversationAsync(IReadOnlyList<MailMessageSummary> messages,
        string destination, CancellationToken ct = default)
    {
        var progress = new Progress<string>(value => StatusText = value);
        return Task.Run(() => new ConversationExportService(_localStore, _imap).ExportAsync(messages, destination, progress, ct), ct);
    }

    public async Task<string> GetMessageActivityReportAsync(MailMessageSummary message, CancellationToken ct = default)
    {
        if (_localStore is not LocalStoreService store) throw new InvalidOperationException("Activity requires the local message store.");
        var rows = await store.LoadMessageActivityAsync(message, ct);
        return "Activity is recorded from feature activation onwards. Up to 90 days / 100,000 events per profile; latest 500 events for this message.\r\n" +
            "Local catalogue observations are not server arrival/deletion confirmations. Messages sharing a Message-ID in this account share their history.\r\n\r\n" +
            (rows.Count == 0 ? "No recorded activity for this message." : string.Join("\r\n\r\n", rows.Select(row => $"{row.When.LocalDateTime:G} — {row.Action}\r\n{row.Description}")));
    }
}
