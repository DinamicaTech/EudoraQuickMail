using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class MainWindow
{
    private string? _spamPromptIdentity;

    private async Task UpdateSpamPromptAsync(MailMessageDetail? detail)
    {
        SpamBlockButton.Visibility = Visibility.Collapsed;
        SpamBadge.Visibility = Visibility.Collapsed;
        if (detail == null || _localStore is not LocalStoreService store) return;
        var classified = await store.IsSpamAsync(detail);
        if (!ReferenceEquals(_vm.MessageDetail, detail)) return;
        if (classified)
        {
            SpamBadge.Visibility = Visibility.Visible;
            return;
        }
        var address = SpamPolicy.Address(detail.From);
        if (address.Length == 0 || detail.IsOutgoing ||
            Application.Current is App { UnsubscribePreferences: { } preferences } && preferences.IsMaintained(address))
            return;
        var identity = $"{detail.AccountId}/{detail.FolderName}/{detail.MessageId}";
        if (_spamPromptIdentity != identity)
        {
            if (!await Task.Run(() => store.OfferSpamPromptAsync(detail))) return;
            _spamPromptIdentity = identity;
        }
        if (ReferenceEquals(_vm.MessageDetail, detail))
            SpamBlockButton.Visibility = Visibility.Visible;
    }

    private async void SpamBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMessage is not { } message || message.IsOutgoing) return;
        var address = SpamPolicy.Address(message.From);
        if (address.Length == 0) return;
        var domain = SpamPolicy.Domain(address);
        var chosen = (bool?)null;
        var window = new Window { Title = "Block / SPAM — all accounts", Owner = this,
            Width = 530, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize };
        window.SetResourceReference(Control.BackgroundProperty, "Theme.WindowBackground");
        window.SetResourceReference(Control.ForegroundProperty, "Theme.TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Move this message to Junk and block future incoming mail in all EQM accounts. Existing mail is not moved.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        var email = new Button { Content = "Block address: " + address, Padding = new Thickness(8) };
        email.Click += (_, _) => { chosen = false; window.DialogResult = true; };
        panel.Children.Add(email);
        panel.Children.Add(new TextBlock { Text = "Only this exact address. Other senders are unaffected.", TextWrapping = TextWrapping.Wrap });
        var all = new Button { Content = "Block domain: " + domain, IsEnabled = SpamPolicy.CanBlockDomain(domain),
            Padding = new Thickness(8), Margin = new Thickness(0,12,0,0) };
        all.Click += (_, _) => { chosen = true; window.DialogResult = true; };
        panel.Children.Add(all);
        panel.Children.Add(new TextBlock { Text = all.IsEnabled
            ? "Every address in this exact domain, including legitimate people and services."
            : "Shared email provider: only individual addresses can be blocked.", TextWrapping = TextWrapping.Wrap });
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0,12,0,0) };
        panel.Children.Add(cancel);
        window.Content = panel;
        if (window.ShowDialog() != true || chosen is null) return;
        try
        {
            var rules = _ruleService.LoadRules().ToList();
            var value = chosen.Value ? domain : address;
            var rule = rules.FirstOrDefault(r => r.IsSpamFilter && r.SpamBlockDomain == chosen.Value &&
                string.Equals(r.FromContains, value, StringComparison.OrdinalIgnoreCase));
            if (rule == null)
            {
                rule = new MailRule { Name = "SPAM: " + value, IsSpamFilter = true,
                    SpamBlockDomain = chosen.Value, FromContains = value };
                rules.Add(rule);
            }
            rule.IsEnabled = true;
            _ruleService.SaveRules(rules);
            await RunWithDelayedIndicatorAsync("Blocking SPAM…", async () =>
            {
                await _ruleService.ApplyRuleToMessagesAsync(rule, [message], _localStore, CancellationToken.None);
                await _vm.RefreshAfterLocalMutationAsync("spam-block");
            });
            SpamBlockButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ShowSpamError(ex); }
    }

    private async void NotSpam_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMessage is not { } message || _localStore is not LocalStoreService store) return;
        try
        {
            if (!await store.IsSpamAsync(message))
            {
                MessageBox.Show(this, "This message has not been classified as SPAM by EQM.", "Not SPAM");
                return;
            }
            var rules = _ruleService.LoadRules().ToList();
            var blocks = rules.Where(r => r.IsSpamFilter && SpamPolicy.Matches(r, message)).ToList();
            var text = "Remove the following blocks, clear SPAM classification and return this message to In?\n\n" +
                string.Join("\n", blocks.Select(r => (r.SpamBlockDomain ? "Entire domain: " : "Address: ") + r.FromContains)) +
                "\n\nDomain blocks affect ALL addresses in that domain, in all accounts.";
            if (MessageBox.Show(this, text, "Not SPAM", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var folders = await _imap.GetFoldersAsync(message.AccountId);
            var inbox = folders.FirstOrDefault(f => f.Kind == SpecialFolderKind.Inbox)
                ?? throw new InvalidOperationException("Inbox is not available.");
            // Move first: failure must not silently remove the block.
            if (message.FolderName != inbox.FullName)
            {
                await _imap.MoveMessagesAsync(message.AccountId, message.FolderName, [message.MessageId], inbox.FullName);
                if (_vm.Accounts.FirstOrDefault(a => a.Id == message.AccountId)?.BackendKind
                    is BackendKind.ImapSmtp or BackendKind.MicrosoftGraph && _localStore is ILocalMailboxStore mailbox)
                    await mailbox.MoveLocalMessagesAsync(message.AccountId, message.FolderName, inbox.FullName, [message.MessageId]);
            }
            await store.SetSpamAsync(message, false);
            var moved = new MailMessageSummary { AccountId = message.AccountId, FolderName = inbox.FullName,
                MessageId = message.MessageId, InternetMessageId = message.InternetMessageId };
            await store.SetSpamAsync(moved, false);
            rules.RemoveAll(r => blocks.Any(b => b.Id == r.Id));
            _ruleService.SaveRules(rules);
            await _vm.RefreshAfterLocalMutationAsync("not-spam");
        }
        catch (Exception ex) { ShowSpamError(ex); }
    }

    private async void EmptySpam_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuFolderNode(sender)?.Folder is not { } folder) return;
        try { await RunWithDelayedIndicatorAsync("Deleting SPAM…", () => _vm.EmptySpamAsync(folder)); }
        catch (Exception ex) { ShowSpamError(ex); }
    }

    private void ShowSpamError(Exception ex)
    {
        LogService.Log("SPAM operation", ex);
        MessageBox.Show(this, ex.Message, "SPAM operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
