using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class AttachmentBrowserWindow : Window
{
    private readonly AttachmentBrowserViewModel _vm;
    private readonly CommandRegistry _commands = new();
    private readonly IInputElement? _origin = Keyboard.FocusedElement;
    public event Func<MailMessageSummary,Task>? GoToMessageRequested;
    public AttachmentBrowserWindow(AttachmentBrowserViewModel vm)
    {
        InitializeComponent(); DataContext = _vm = vm;
        _commands.Register(new CommandDefinition("attachments.close", "Mail", "Close attachment explorer", Close, Key.Escape));
        _commands.Register(new CommandDefinition("attachments.search", "Mail", "Search attachments", () => Search_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.save", "Mail", "Save selected attachments", () => Save_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.locate", "Mail", "Locate missing attachment", () => Find_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.go", "Mail", "Go to message", () => Go_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.copyPath", "Mail", "Copy original path", () => Copy_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.recover", "Mail", "Recover a copy", () => Recover_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.relink", "Mail", "Use candidate location", () => Relink_Click(this,new RoutedEventArgs())));
        _commands.Register(new CommandDefinition("attachments.cancel", "Mail", "Cancel operation", _vm.Cancel));
        Loaded += async (_,_) => { SearchBox.Focus(); await _vm.SearchAsync(); };
        Closing += (_,_) => _vm.Dispose();
        Closed += (_,_) => { if (_origin != null) Keyboard.Focus(_origin); };
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await _vm.SearchAsync();
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        var selected = AttachmentsGrid.SelectedItems.Cast<AttachmentBrowserItem>().ToList();
        if (selected.Count == 0) return;
        var picker = new OpenFolderDialog { Title = "Save selected attachments" };
        if (picker.ShowDialog(this) == true) await _vm.SaveAsync(selected,picker.FolderName);
    }
    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } item || _vm.IsBusy) return;
        try { if (GoToMessageRequested != null) await GoToMessageRequested(item.Message); }
        catch (Exception ex) { _vm.Status = ex.Message; }
    }
    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || _vm.Selected == null) return;
        var picker = new OpenFolderDialog { Title = "Search this directory for the missing attachment" };
        if (picker.ShowDialog(this) == true) await _vm.FindMissingAsync(picker.FolderName);
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (_vm.Selected is { } item) Clipboard.SetText(item.SourcePath); }
        catch (Exception ex) { _vm.Status = ex.Message; }
    }
    private async void Recover_Click(object sender, RoutedEventArgs e) => await ConfirmRepairAsync(true);
    private async void Relink_Click(object sender, RoutedEventArgs e) => await ConfirmRepairAsync(false);
    private async Task ConfirmRepairAsync(bool copy)
    {
        if (_vm.IsBusy || _vm.Candidate is not { } candidate) return;
        var text = $"{candidate.Path}\n\n{candidate.Evidence}\n\n" +
            (copy ? "Copy this file into EQM and replace this attachment's local reference?" : "Use this location for the attachment? It must remain accessible in the future.") +
            "\n\nName/size alone do not prove identity. No other messages or backup files will be modified.";
        if (MessageBox.Show(this,text,"Confirm attachment repair",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No) == MessageBoxResult.Yes)
            await _vm.RepairAsync(copy);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _vm.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            UIElement[] stops = [SearchBox,AttachmentsGrid,GoButton,CandidatesGrid,CloseButton];
            var current = Array.FindIndex(stops,s => s.IsKeyboardFocusWithin);
            var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var next = current < 0 ? (backwards ? stops.Length-1 : 0) : (current + (backwards ? stops.Length-1 : 1)) % stops.Length;
            stops[next].Focus(); e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control|ModifierKeys.Shift))
        { new CommandPaletteWindow(_commands) { Owner = this }.ShowDialog(); e.Handled = true; }
        else if (_commands.FindByGesture(e.Key,Keyboard.Modifiers) is { } command) { command.Execute(); e.Handled = true; }
    }
}
