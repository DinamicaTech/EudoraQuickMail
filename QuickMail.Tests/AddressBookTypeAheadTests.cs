using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Deterministic coverage for the text exposed to WPF's built-in list type-ahead.
/// Keyboard routing and type-ahead wiring are covered by TypeAheadWiringTests and
/// TypeAheadLogicTests; the former opt-in input tests duplicated those checks and
/// were permanently skipped because their synthetic TextInput timing was flaky.
/// </summary>
public class AddressBookTypeAheadTests
{
    [Fact]
    public void TypeAheadText_PrefersName_FallsBackToAddress()
    {
        Assert.Equal("Bob Baker",
            new ContactModel { DisplayName = "Bob Baker", EmailAddress = "bob@example.com" }.TypeAheadText);
        Assert.Equal("zeta@example.com",
            new ContactModel { DisplayName = "", EmailAddress = "zeta@example.com" }.TypeAheadText);
        Assert.Equal("zeta@example.com",
            new ContactModel { DisplayName = "   ", EmailAddress = "zeta@example.com" }.TypeAheadText);
    }
}
