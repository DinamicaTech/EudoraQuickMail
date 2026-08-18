using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public sealed class CatalanSpellCheckServiceTests
{
    [Fact]
    public void FindMisspellings_UsesSoftcatalaDictionaryAndReturnsSuggestions()
    {
        var service = new CatalanSpellCheckService(null);

        var errors = service.FindMisspellings(["aquesta", "paraulla"]);

        Assert.DoesNotContain("aquesta", errors.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True(errors.TryGetValue("paraulla", out var suggestions));
        Assert.Contains(suggestions!, value => value.Equals("paraula", StringComparison.OrdinalIgnoreCase));
    }
}
