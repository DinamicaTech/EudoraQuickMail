using DinamicaBackup;

public sealed class NotificationAddressTests
{
    [Fact] public void LegacySenderIsSplitWithoutLosingAlias()
    {
        var options = new NotificationOptions { From = "Dinamica Backup <backup@example.com>" };
        NotificationAddresses.NormalizeLegacySender(options);
        Assert.Equal("backup@example.com", options.From);
        Assert.Equal("Dinamica Backup", options.SenderDisplayName);
        Assert.Equal("Dinamica Backup", NotificationAddresses.Sender(options).Name);
    }
    [Fact] public void ExplicitAliasTakesPrecedence()
    {
        var options = new NotificationOptions { From = "Old <backup@example.com>", SenderDisplayName = "New" };
        NotificationAddresses.NormalizeLegacySender(options);
        Assert.Equal("New", NotificationAddresses.Sender(options).Name);
    }
    [Theory]
    [InlineData("Dinamica Backup")]
    [InlineData("")]
    [InlineData("backup@")]
    public void InvalidSenderExplainsWhichFieldToFix(string value)
    {
        var error = Assert.Throws<InvalidDataException>(() => NotificationAddresses.Sender(new NotificationOptions { From = value }));
        Assert.Contains("Sender email", error.Message);
    }
    [Fact] public void PlainEmailNeedsNoAlias()
    {
        var sender = NotificationAddresses.Sender(new NotificationOptions { From = "backup@example.com" });
        Assert.Equal("", sender.Name); Assert.Equal("backup@example.com", sender.Address);
    }
    [Fact] public void RecipientsAreValidatedWithFieldSpecificError()
    {
        Assert.Equal(2, NotificationAddresses.Recipients(new NotificationOptions { To = "one@example.com, two@example.com" }).Mailboxes.Count());
        var error = Assert.Throws<InvalidDataException>(() => NotificationAddresses.Recipients(new NotificationOptions { To = "Not an email" }));
        Assert.Contains("Report recipients", error.Message);
    }
}
