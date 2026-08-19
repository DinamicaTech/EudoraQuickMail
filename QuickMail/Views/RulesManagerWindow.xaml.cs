using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>Inverts a boolean value. Used for IsReadOnly/IsTabStop bindings.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}

public partial class RulesManagerWindow : Window
{
    private readonly RulesManagerViewModel _vm;
    private readonly IEnumerable<AccountModel> _accounts;
    private readonly IReadOnlyDictionary<Guid, List<MailFolderModel>> _cachedFolders;
    private readonly Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? _folderCreator;

    public RulesManagerWindow(
        RulesManagerViewModel vm,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        Func<Guid, string?, string, Task<IReadOnlyList<MailFolderModel>?>>? folderCreator = null)
    {
        InitializeComponent();
        _vm = vm;
        _accounts = accounts;
        _cachedFolders = cachedFolders;
        _folderCreator = folderCreator;
        DataContext = vm;

        // Wire VM events
        vm.CloseRequested += OnCloseRequested;
        vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;
        vm.AnnouncementRequested += OnAnnouncementRequested;
        vm.PickFolderRequested += OnPickFolderRequested;

        // Focus the client rule list on open (#348).
        Loaded += (_, _) =>
        {
            Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.9);
            FocusFirstRule();
        };
    }

    /// <summary>Moves keyboard focus to the next (or previous) window pane for F6 / Shift+F6.</summary>
    private void CyclePane(bool forward)
    {
        var stops = new List<UIElement> { RuleListBox, MainStatusText };

        // Find where focus currently sits (walk up from the focused element to a known pane).
        var current = -1;
        for (var node = Keyboard.FocusedElement as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement el && (current = stops.IndexOf(el)) >= 0) break;
        }

        var next = current < 0
            ? 0
            : (current + (forward ? 1 : stops.Count - 1)) % stops.Count;
        stops[next].Focus();
    }

    private void FocusFirstRule()
    {
        if (RuleListBox.Items.Count == 0)
        {
            RuleListBox.Focus();
            return;
        }

        if (RuleListBox.SelectedIndex < 0)
            RuleListBox.SelectedIndex = 0;

        RuleListBox.UpdateLayout();
        if (RuleListBox.ItemContainerGenerator.ContainerFromIndex(RuleListBox.SelectedIndex) is ListBoxItem item)
            item.Focus();
        else
            RuleListBox.Focus();
    }

    private string? OnPickFolderRequested(Guid? accountId, string? currentFolder)
    {
        var picker = FolderPickerWindow.ForRuleTarget(
            _accounts, _cachedFolders, accountId, currentFolder,
            title: "Choose Target Folder",
            folderCreator: _folderCreator,
            defaultNewFolderName: SuggestedFolderName(_vm.SelectedRule?.FromContains));
        picker.Owner = this;

        if (picker.ShowDialog() == true && picker.SelectedFolder is MailFolderModel folder)
        {
            return folder.FullName;
        }
        return null;
    }

    private static string SuggestedFolderName(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return string.Empty;
        var at = from.LastIndexOf('@');
        if (at < 0 || at + 1 >= from.Length) return string.Empty;
        var domain = from[(at + 1)..].Trim(' ', '<', '>', '"').Split(',')[0];
        var label = domain.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return label.Length == 0 ? string.Empty : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private void FromContainsTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedRule is not { } rule || string.IsNullOrWhiteSpace(rule.FromContains)) return;

        var value = rule.FromContains.Trim();
        var at = value.LastIndexOf('@');
        if (at < 0 || at + 1 >= value.Length) return;

        var domain = value[(at + 1)..]
            .TrimStart()
            .Split([' ', '\t', '\r', '\n', '>', '<', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(domain)) return;

        // MailRule is deliberately a plain persisted model, not an observable view-model. Setting
        // only its property would therefore leave the bound TextBox showing the old address.
        FromContainsTextBox.Text = "@" + domain;
        FromContainsTextBox.CaretIndex = FromContainsTextBox.Text.Length;
        e.Handled = true;
    }

    private void OnCloseRequested()
    {
        Close();
    }

    /// <summary>
    /// Adds a rule prefilled from a message and focuses the list. Called when Ctrl+Shift+T is
    /// pressed while this (modeless) window is already open, so the template isn't dropped.
    /// </summary>
    public void PrefillFromTemplate(MailRule template)
    {
        _vm.AddPrefilledRule(template);
        RuleListBox.Focus();
    }

    private bool OnConfirmDeleteRequested(string message, string title)
    {
        return MessageBox.Show(
            message, title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void OnAnnouncementRequested(string text, AnnouncementCategory category)
    {
        AccessibilityHelper.Announce(this, text, interrupt: true, category: category);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Dialog-local shortcuts (not registered in CommandRegistry — these are
        // scoped to this window only, same pattern as other dialogs).
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.NewRuleCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // This window is shown modeless (see MainWindow.OpenRulesManager) to avoid the
        // modal-over-WebView2 dispatcher deadlock. A modeless window has no DialogResult,
        // so the Close button's IsCancel="True" no longer closes it on Escape — wire that
        // explicitly here. Step aside when an open combo dropdown needs Escape to dismiss
        // itself, so we don't steal it (matches ComposeWindow's guard).
        // F6 / Shift+F6 cycle between the window's panes (New Window Checklist). Stops are the client
        // rule list and the status line — so a keyboard/screen-reader user can reach every region,
        // including the status to re-read counts.
        if (e.Key == Key.F6)
        {
            CyclePane(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (AccountScopeCombo.IsDropDownOpen || ActionCombo.IsDropDownOpen)
                return;

            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.CloseRequested -= OnCloseRequested;
        _vm.ConfirmDeleteRequested -= OnConfirmDeleteRequested;
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        _vm.PickFolderRequested -= OnPickFolderRequested;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.CancelPendingEdits();
        base.OnClosing(e);
    }
}
