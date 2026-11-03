using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using QuickMail.ViewModels;

namespace QuickMail.Views;

internal sealed class ConnectionTestIndicator
{
    private readonly Window _owner;
    private readonly AccountEditorViewModel _vm;
    private SlowOperationWindow? _window;

    internal ConnectionTestIndicator(Window owner, AccountEditorViewModel vm)
    {
        _owner = owner; _vm = vm;
        vm.PropertyChanged += Changed;
        owner.Closed += (_, _) => { vm.PropertyChanged -= Changed; vm.CancelConnectionTest(); _window?.Close(); };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_vm.IsTestingConnection)) return;
        if (!_vm.IsTestingConnection) { _window?.Close(); _window = null; return; }
        if (!_owner.IsVisible) return;
        var elapsed = Stopwatch.StartNew();
        _window = new SlowOperationWindow("Test Connection", () =>
            $"{_vm.ConnectionTestStage}\n\nElapsed: {elapsed.Elapsed.TotalSeconds:N0} s\nNo email will be sent.", _vm.CancelConnectionTest) { Owner = _owner };
        _window.CancelOperation.Content = "Cancel test";
        var indicator = _window;
        indicator.Closed += (_, _) => { if (_vm.IsTestingConnection) _vm.CancelConnectionTest(); };
        _window.Show();
    }
}
