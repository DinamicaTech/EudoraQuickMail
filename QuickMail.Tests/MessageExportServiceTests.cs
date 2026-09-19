using QuickMail.Models;
using QuickMail.Services;
using System.IO;

namespace QuickMail.Tests;

public class MessageExportServiceTests
{
    [Fact]
    public void Headers_PrefersOriginalRawHeaders()
    {
        var detail = new MailMessageDetail
        {
            From = "fallback@example.com",
            RawHeaders = "X-Test: preserved\r\nFrom: original@example.com\r\n"
        };

        var headers = MessageExportService.Headers(detail);

        Assert.Contains("X-Test: preserved", headers);
        Assert.DoesNotContain("fallback@example.com", headers);
    }

    [Fact]
    public void Html_AddsReadableMetadataAndPreservesBody()
    {
        var detail = new MailMessageDetail
        {
            Subject = "A < B",
            From = "Alice <alice@example.com>",
            To = "bob@example.com",
            Date = new DateTimeOffset(2026, 9, 17, 10, 30, 0, TimeSpan.Zero),
            HtmlBody = "<html><body><p>Hello world</p></body></html>"
        };

        var html = MessageExportService.Html(detail);

        Assert.Contains("<b>Subject:</b> A &lt; B", html);
        Assert.Contains("<p>Hello world</p>", html);
        Assert.Contains("alice@example.com", html);
    }

    [Fact]
    public async Task WriteEmlAsync_ProducesParseableMessageWithAttachment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quickmail-export-{Guid.NewGuid():N}.eml");
        try
        {
            var detail = new MailMessageDetail
            {
                MessageId = "42",
                AccountId = Guid.NewGuid(),
                FolderName = "In",
                From = "alice@example.com",
                To = "bob@example.com",
                Subject = "Export test",
                Date = DateTimeOffset.UtcNow,
                PlainTextBody = "Hello",
                Attachments =
                [
                    new AttachmentModel
                    {
                        FileName = "report.pdf",
                        ContentType = "application/pdf",
                        Content = [1, 2, 3, 4]
                    }
                ]
            };

            await MessageExportService.WriteEmlAsync(detail, path, new StubImapMailService(),
                TestContext.Current.CancellationToken);
            var parsed = await MimeKit.MimeMessage.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("Export test", parsed.Subject);
            Assert.Equal("Hello", parsed.TextBody);
            Assert.Single(parsed.Attachments);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
