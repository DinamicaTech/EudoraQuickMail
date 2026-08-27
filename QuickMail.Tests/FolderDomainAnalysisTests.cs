using System.IO;
using Microsoft.Data.Sqlite;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class FolderDomainAnalysisTests
{
    [Theory]
    [InlineData("True Nutra Support <support@MyTrueNutra.COM>", "mytruenutra.com")]
    [InlineData("sender@example.com", "example.com")]
    [InlineData("Softaculous <admin@>", SenderDomainExtractor.MissingDomain)]
    [InlineData("", SenderDomainExtractor.MissingDomain)]
    public void SenderDomainExtractor_HandlesDisplayAndMalformedAddresses(string from, string expected) =>
        Assert.Equal(expected, SenderDomainExtractor.Extract(from));

    [Fact]
    public async Task Analysis_UsesWholeRecursiveFolderScopeAndOrdersByCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuickMail-folder-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalStoreService(new ProfileContext(directory));
            store.Initialize();
            var account = Guid.NewGuid();
            var sequence = 0;
            async Task Add(string folder, string from)
            {
                sequence++;
                await store.SaveLocalMessageAsync(new MailMessageDetail
                {
                    AccountId = account,
                    FolderName = folder,
                    MessageId = "message-" + sequence,
                    From = from,
                    Subject = "Test",
                    Date = DateTimeOffset.UtcNow.AddMinutes(sequence),
                    PlainTextBody = "Body",
                }, TestContext.Current.CancellationToken);
            }

            await Add("Inbox", "One <one@example.com>");
            await Add("Inbox", "Two <two@example.com>");
            await Add("Inbox", "Three <three@example.com>");
            await Add("Inbox/Subfolder", "One <one@other.net>");
            await Add("Inbox/Subfolder", "Two <two@other.net>");
            await Add("Inbox", "Malformed <admin@>");
            await Add("Archive", "Outside <outside@example.com>");

            var rows = await store.AnalyzeFolderDomainsAsync(new FolderDomainAnalysisQuery(
                AccountId: account, FolderName: "Inbox", IncludeDescendants: true),
                TestContext.Current.CancellationToken);

            Assert.Collection(rows,
                row => { Assert.Equal("example.com", row.Domain); Assert.Equal(3, row.MessageCount); },
                row => { Assert.Equal("other.net", row.Domain); Assert.Equal(2, row.MessageCount); },
                row => { Assert.Equal(SenderDomainExtractor.MissingDomain, row.Domain); Assert.Equal(1, row.MessageCount); });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
