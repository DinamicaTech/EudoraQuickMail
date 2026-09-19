namespace QuickMail.Tests;

/// <summary>
/// Ensures the process-wide WPF application is available before a UI test
/// constructs windows or resolves application resources.
/// </summary>
public abstract class WpfTestBase
{
    protected WpfTestBase() => WpfTestApplication.EnsureStarted();
}
