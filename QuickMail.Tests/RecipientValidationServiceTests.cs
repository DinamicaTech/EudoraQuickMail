using System.IO;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class RecipientValidationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QuickMail-recipient-validation-" + Guid.NewGuid().ToString("N"));

    private sealed class StubResolver(RecipientDomainResult result) : IRecipientDomainResolver
    {
        public int Calls { get; private set; }
        public Task<RecipientDomainResult> ResolveAsync(string domain, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubProbe(RecipientSmtpResult result) : IRecipientSmtpProbe
    {
        public int Calls { get; private set; }
        public Task<RecipientSmtpResult> ProbeAsync(
            IReadOnlyList<string> mailHosts,
            string address,
            string? senderAddress,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task BadSyntaxIsRedWithoutNetworkTraffic()
    {
        var resolver = new StubResolver(AcceptingDomain());
        var probe = new StubProbe(Accepted());
        var service = Create(resolver, probe);

        var result = await service.ClassifyAsync("not-an-address");

        Assert.Equal(RecipientValidationState.Invalid, result.State);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task MailDomainIsYellowAndOffersTheExplicitProbe()
    {
        var service = Create(new StubResolver(AcceptingDomain()), new StubProbe(Accepted()));

        var result = await service.ClassifyAsync("person@example.com");

        Assert.Equal(RecipientValidationState.Indeterminate, result.State);
        Assert.True(result.CanProbe);
        Assert.Contains("MX", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullMxOrMissingDomainIsRedWithoutSmtpProbe()
    {
        var probe = new StubProbe(Accepted());
        var service = Create(new StubResolver(new(
            RecipientDomainState.RejectsMail, [], "Domain has Null MX.")), probe);

        var result = await service.ProbeAsync("person@example.com", "sender@example.net");

        Assert.Equal(RecipientValidationState.Invalid, result.State);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task AcceptedRecipientTurnsGreenAndIsNeverProbedTwice()
    {
        var resolver = new StubResolver(AcceptingDomain());
        var probe = new StubProbe(Accepted());
        var service = Create(resolver, probe);

        var first = await service.ProbeAsync("person@example.com", "sender@example.net");
        var second = await service.ProbeAsync("PERSON@example.com", "sender@example.net");

        Assert.Equal(RecipientValidationState.Valid, first.State);
        Assert.Equal(RecipientValidationState.Valid, second.State);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task DefinitiveResultSurvivesAServiceRestart()
    {
        var firstProbe = new StubProbe(Accepted());
        var first = Create(new StubResolver(AcceptingDomain()), firstProbe);
        await first.ProbeAsync("person@example.com", "sender@example.net");

        var secondResolver = new StubResolver(new(
            RecipientDomainState.RejectsMail, [], "Must not be called."));
        var secondProbe = new StubProbe(new(RecipientSmtpOutcome.Rejected, "Must not be called."));
        var reopened = Create(secondResolver, secondProbe);
        var cached = await reopened.ClassifyAsync("person@example.com");

        Assert.Equal(RecipientValidationState.Valid, cached.State);
        Assert.Equal(0, secondResolver.Calls);
        Assert.Equal(0, secondProbe.Calls);
        Assert.True(File.Exists(Path.Combine(_directory, "recipient-validation.json")));
    }

    [Fact]
    public async Task TemporarySmtpAnswerStaysYellowAndIsNotImmediatelyRepeated()
    {
        var probe = new StubProbe(new(RecipientSmtpOutcome.Inconclusive, "Greylisted."));
        var service = Create(new StubResolver(AcceptingDomain()), probe);

        var first = await service.ProbeAsync("person@example.com", "sender@example.net");
        var second = await service.ProbeAsync("person@example.com", "sender@example.net");

        Assert.Equal(RecipientValidationState.Indeterminate, first.State);
        Assert.False(first.CanProbe);
        Assert.Equal(RecipientValidationState.Indeterminate, second.State);
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(250, "250 2.1.5 Recipient OK", (int)RecipientSmtpOutcome.Accepted)]
    [InlineData(550, "550 5.1.1 User unknown", (int)RecipientSmtpOutcome.Rejected)]
    [InlineData(553, "553 5.1.3 Bad destination mailbox", (int)RecipientSmtpOutcome.Rejected)]
    [InlineData(550, "550 5.7.1 Rejected by policy", (int)RecipientSmtpOutcome.Inconclusive)]
    [InlineData(450, "450 4.2.0 Greylisted", (int)RecipientSmtpOutcome.Inconclusive)]
    public void SmtpRepliesAreClassifiedConservatively(
        int code,
        string reply,
        int expected)
    {
        var result = SmtpRecipientProbe.ClassifyRecipientReply("mx.example.com", code, reply);
        Assert.Equal((RecipientSmtpOutcome)expected, result.Outcome);
    }

    private RecipientValidationService Create(
        IRecipientDomainResolver resolver,
        IRecipientSmtpProbe probe) =>
        new(new ProfileContext(_directory), resolver, probe);

    private static RecipientDomainResult AcceptingDomain() =>
        new(RecipientDomainState.AcceptsMail, ["mx.example.com"], "The domain has a valid MX record.");

    private static RecipientSmtpResult Accepted() =>
        new(RecipientSmtpOutcome.Accepted, "The SMTP server accepted this recipient.");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
