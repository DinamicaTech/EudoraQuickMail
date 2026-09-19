using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace QuickMail.Tests;

/// <summary>
/// Owns the single process-wide WPF application used by the UI tests.
///
/// <para>
/// <c>WpfFact</c> creates and tears down a dispatcher for every test. Creating
/// <see cref="Application"/> on one of those temporary dispatchers leaves WPF's
/// AppDomain-wide application slot permanently used after that test ends. The
/// following tests then either see an application that is shutting down or fail
/// while trying to create a second one.
/// </para>
///
/// <para>
/// Keep the application on a dedicated STA dispatcher for the lifetime of the
/// test process instead. Individual tests may still use their own WPF dispatcher.
/// </para>
/// </summary>
internal static class WpfTestApplication
{
    private static readonly object Sync = new();
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? _thread;
    private static Exception? _startupError;

    internal static void EnsureStarted()
    {
        lock (Sync)
        {
            if (_thread == null)
            {
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "QuickMail WPF test application",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        Ready.Wait();
        if (_startupError != null)
            throw new InvalidOperationException("The WPF test application could not start.", _startupError);
    }

    private static void Run()
    {
        try
        {
            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            foreach (var style in new[] { "AccessibleStyles", "ThemedControls" })
            {
                var uri = new Uri(
                    $"pack://application:,,,/QuickMail;component/Styles/{style}.xaml",
                    UriKind.Absolute);
                if (app.Resources.MergedDictionaries.All(d => d.Source != uri))
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
            }

            Ready.Set();
            Dispatcher.Run();
            GC.KeepAlive(app);
        }
        catch (Exception ex)
        {
            _startupError = ex;
            Ready.Set();
        }
    }
}
