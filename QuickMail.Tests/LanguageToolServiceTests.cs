using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public sealed class LanguageToolServiceTests
{
    [Fact]
    public void ParseAndApplyPreferredReplacements_UsesLanguageToolOffsetsFromRightToLeft()
    {
        const string json = """
            {"matches":[
              {"message":"Agreement","offset":3,"length":2,"replacements":[{"value":"are"}],"rule":{"id":"A"}},
              {"message":"Article","offset":0,"length":2,"replacements":[{"value":"They"}],"rule":{"id":"B"}}
            ]}
            """;

        var issues = LanguageToolService.ParseIssues(json);
        var corrected = LanguageToolService.ApplyPreferredReplacements("He is ready", issues);

        Assert.Equal(2, issues.Count);
        Assert.Equal("They are ready", corrected);
    }

    [Fact]
    public void ApplyPreferredReplacements_IgnoresAdviceWithoutAutomaticReplacement()
    {
        var issues = new[] { new GrammarIssue(0, 4, "Consider rephrasing", "STYLE", []) };
        Assert.Equal("Text", LanguageToolService.ApplyPreferredReplacements("Text", issues));
    }
}
