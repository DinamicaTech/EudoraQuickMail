using Xunit;

// QuickMail's test assembly exercises WPF windows, Application.Current resources,
// process-wide configuration paths, and static services. Those objects are not
// isolated between test classes, so running different classes concurrently causes
// false failures, locked files, and occasionally a test-host deadlock. Keep the
// suite deterministic; individual tests can still test their own parallel work.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuickMail.Tests;

// Marks all WPF window-loading test classes as belonging to a single collection,
// which prevents xUnit from running them in parallel. Parallel XAML InitializeComponent()
// calls race inside PackagePart.CleanUpRequestedStreamsList() causing flaky failures.
[CollectionDefinition("WpfTests")]
public class WpfTestsCollection { }
