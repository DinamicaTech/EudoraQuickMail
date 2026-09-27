using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;
using QuickMail.Helpers;

namespace QuickMail.Services;

public enum RecipientValidationState
{
    Valid,
    Indeterminate,
    Invalid
}

public sealed record RecipientValidationResult(
    RecipientValidationState State,
    string Detail,
    bool CanProbe = false);

public interface IRecipientValidationService
{
    Task<RecipientValidationResult> ClassifyAsync(string address, CancellationToken ct = default);
    Task<RecipientValidationResult> ProbeAsync(
        string address,
        string? senderAddress,
        CancellationToken ct = default);
}

internal enum RecipientDomainState { AcceptsMail, RejectsMail, Inconclusive }

internal sealed record RecipientDomainResult(
    RecipientDomainState State,
    IReadOnlyList<string> MailHosts,
    string Detail);

internal interface IRecipientDomainResolver
{
    Task<RecipientDomainResult> ResolveAsync(string domain, CancellationToken ct);
}

internal enum RecipientSmtpOutcome { Accepted, Rejected, Inconclusive }

internal sealed record RecipientSmtpResult(RecipientSmtpOutcome Outcome, string Detail);

internal interface IRecipientSmtpProbe
{
    Task<RecipientSmtpResult> ProbeAsync(
        IReadOnlyList<string> mailHosts,
        string address,
        string? senderAddress,
        CancellationToken ct);
}

/// <summary>
/// Gives compose recipients an evidence-based state without ever sending a message. Syntax and
/// DNS checks happen automatically; the deliberately more intrusive SMTP RCPT check runs only
/// after an explicit click on a yellow chip. Definitive SMTP results are remembered per profile.
/// </summary>
public sealed class RecipientValidationService : IRecipientValidationService, IDisposable
{
    private const int MaxProbesPerHour = 10;
    private static readonly TimeSpan DnsResultLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan TemporaryResultLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private sealed class CacheEntry
    {
        public string Address { get; set; } = string.Empty;
        public RecipientValidationState State { get; set; }
        public string Detail { get; set; } = string.Empty;
        public bool CanProbe { get; set; }
        public bool WasSmtpProbed { get; set; }
        public DateTimeOffset CheckedUtc { get; set; }
        public DateTimeOffset? ExpiresUtc { get; set; }
    }

    private readonly string _cachePath;
    private readonly IRecipientDomainResolver _domainResolver;
    private readonly IRecipientSmtpProbe _smtpProbe;
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly Queue<DateTimeOffset> _recentProbes = new();
    private readonly Dictionary<string, CacheEntry> _entries;

    public RecipientValidationService(ProfileContext profile)
        : this(profile, new RecipientDomainResolver(), new SmtpRecipientProbe())
    {
    }

    internal RecipientValidationService(
        ProfileContext profile,
        IRecipientDomainResolver domainResolver,
        IRecipientSmtpProbe smtpProbe)
    {
        _cachePath = Path.Combine(profile.ProfileDir, "recipient-validation.json");
        _domainResolver = domainResolver;
        _smtpProbe = smtpProbe;
        _entries = LoadCache();
    }

    public async Task<RecipientValidationResult> ClassifyAsync(
        string address,
        CancellationToken ct = default)
    {
        if (!TryNormalize(address, out var normalized, out var domain))
            return new(RecipientValidationState.Invalid,
                "Invalid email address syntax. Check the recipient before sending.");

        if (TryGetCurrent(normalized, out var cached)) return ToResult(cached);

        RecipientDomainResult domainResult;
        try
        {
            domainResult = await _domainResolver.ResolveAsync(domain, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(RecipientValidationState.Indeterminate,
                "The mail domain check timed out. Click to try an SMTP check.", CanProbe: true);
        }
        catch (Exception ex)
        {
            LogService.Log($"Recipient DNS validation failed for {domain}", ex);
            return new(RecipientValidationState.Indeterminate,
                "The mail domain could not be checked. Click to try an SMTP check.", CanProbe: true);
        }

        var result = domainResult.State switch
        {
            RecipientDomainState.RejectsMail => new RecipientValidationResult(
                RecipientValidationState.Invalid, domainResult.Detail),
            RecipientDomainState.AcceptsMail => new RecipientValidationResult(
                RecipientValidationState.Indeterminate,
                domainResult.Detail + " The mailbox itself has not been verified. Click to run a one-time SMTP check.",
                CanProbe: true),
            _ => new RecipientValidationResult(
                RecipientValidationState.Indeterminate, domainResult.Detail, CanProbe: true)
        };

        Store(normalized, result, wasSmtpProbed: false, DnsResultLifetime);
        return result;
    }

    public async Task<RecipientValidationResult> ProbeAsync(
        string address,
        string? senderAddress,
        CancellationToken ct = default)
    {
        if (!TryNormalize(address, out var normalized, out var domain))
            return new(RecipientValidationState.Invalid,
                "Invalid email address syntax. Check the recipient before sending.");

        if (TryGetCurrent(normalized, out var cached) && cached.WasSmtpProbed)
            return ToResult(cached);

        await _probeGate.WaitAsync(ct);
        try
        {
            // A second compose window may have completed the same check while this caller waited.
            if (TryGetCurrent(normalized, out cached) && cached.WasSmtpProbed)
                return ToResult(cached);

            if (!TryConsumeProbeSlot())
                return new(RecipientValidationState.Indeterminate,
                    "SMTP checking is temporarily limited to ten recipients per hour. Try again later.",
                    CanProbe: true);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            RecipientDomainResult domainResult;
            try
            {
                domainResult = await _domainResolver.ResolveAsync(domain, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var timedOut = new RecipientValidationResult(RecipientValidationState.Indeterminate,
                    "The SMTP check timed out. No conclusion was stored; you can retry after 24 hours.");
                Store(normalized, timedOut, wasSmtpProbed: true, TemporaryResultLifetime);
                return timedOut;
            }
            catch (Exception ex)
            {
                LogService.Log($"Recipient SMTP DNS lookup failed for {domain}", ex);
                var failed = new RecipientValidationResult(RecipientValidationState.Indeterminate,
                    "The destination mail server could not be resolved. You can retry after 24 hours.");
                Store(normalized, failed, wasSmtpProbed: true, TemporaryResultLifetime);
                return failed;
            }

            if (domainResult.State == RecipientDomainState.RejectsMail)
            {
                var rejectedDomain = new RecipientValidationResult(
                    RecipientValidationState.Invalid, domainResult.Detail);
                Store(normalized, rejectedDomain, wasSmtpProbed: true, lifetime: null);
                return rejectedDomain;
            }

            if (domainResult.MailHosts.Count == 0)
            {
                var unresolved = new RecipientValidationResult(RecipientValidationState.Indeterminate,
                    domainResult.Detail + " No SMTP connection was attempted.");
                Store(normalized, unresolved, wasSmtpProbed: true, TemporaryResultLifetime);
                return unresolved;
            }

            RecipientSmtpResult smtpResult;
            try
            {
                smtpResult = await _smtpProbe.ProbeAsync(
                    domainResult.MailHosts, normalized, senderAddress, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                smtpResult = new(RecipientSmtpOutcome.Inconclusive,
                    "The SMTP check timed out. You can retry after 24 hours.");
            }
            catch (Exception ex)
            {
                LogService.Log($"Recipient SMTP validation failed for {normalized}", ex);
                smtpResult = new(RecipientSmtpOutcome.Inconclusive,
                    "The SMTP server could not be queried. You can retry after 24 hours.");
            }

            var result = smtpResult.Outcome switch
            {
                RecipientSmtpOutcome.Accepted => new RecipientValidationResult(
                    RecipientValidationState.Valid,
                    smtpResult.Detail + " Final delivery is not guaranteed because the domain may accept all recipients."),
                RecipientSmtpOutcome.Rejected => new RecipientValidationResult(
                    RecipientValidationState.Invalid, smtpResult.Detail),
                _ => new RecipientValidationResult(
                    RecipientValidationState.Indeterminate, smtpResult.Detail)
            };

            Store(normalized, result, wasSmtpProbed: true,
                smtpResult.Outcome == RecipientSmtpOutcome.Inconclusive
                    ? TemporaryResultLifetime
                    : null);
            return result;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private bool TryConsumeProbeSlot()
    {
        var now = DateTimeOffset.UtcNow;
        while (_recentProbes.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromHours(1))
            _recentProbes.Dequeue();
        if (_recentProbes.Count >= MaxProbesPerHour) return false;
        _recentProbes.Enqueue(now);
        return true;
    }

    private bool TryGetCurrent(string address, out CacheEntry entry)
    {
        lock (_cacheGate)
        {
            if (_entries.TryGetValue(address, out entry!)
                && (entry.ExpiresUtc is null || entry.ExpiresUtc > DateTimeOffset.UtcNow))
                return true;

            if (entry is not null)
            {
                _entries.Remove(address);
                SaveCache();
            }
            entry = null!;
            return false;
        }
    }

    private void Store(
        string address,
        RecipientValidationResult result,
        bool wasSmtpProbed,
        TimeSpan? lifetime)
    {
        lock (_cacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            _entries[address] = new CacheEntry
            {
                Address = address,
                State = result.State,
                Detail = result.Detail,
                CanProbe = result.CanProbe,
                WasSmtpProbed = wasSmtpProbed,
                CheckedUtc = now,
                ExpiresUtc = lifetime is null ? null : now.Add(lifetime.Value)
            };
            SaveCache();
        }
    }

    private Dictionary<string, CacheEntry> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return new(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<CacheEntry[]>(File.ReadAllText(_cachePath)) ?? [];
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value.Address))
                .GroupBy(value => value.Address, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(value => value.CheckedUtc).First())
                .ToDictionary(value => value.Address, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogService.Log("Load recipient validation cache", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveCache()
    {
        try
        {
            AtomicJsonFile.Write(_cachePath, _entries.Values.OrderBy(value => value.Address).ToArray());
        }
        catch (Exception ex)
        {
            LogService.Log("Save recipient validation cache", ex);
        }
    }

    private static RecipientValidationResult ToResult(CacheEntry entry) =>
        new(entry.State, entry.Detail,
            entry.State == RecipientValidationState.Indeterminate && entry.CanProbe && !entry.WasSmtpProbed);

    private static bool TryNormalize(string input, out string address, out string domain)
    {
        address = string.Empty;
        domain = string.Empty;
        if (!EmailAddressValidator.TryNormalize(input, out var parsed)) return false;

        var at = parsed.LastIndexOf('@');
        if (at <= 0 || at == parsed.Length - 1) return false;
        var local = parsed[..at];
        var rawDomain = parsed[(at + 1)..];
        try
        {
            if (rawDomain.StartsWith('[') && rawDomain.EndsWith(']'))
                domain = rawDomain;
            else
                domain = new IdnMapping().GetAscii(rawDomain).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        address = local + "@" + domain;
        return true;
    }

    public void Dispose() => _probeGate.Dispose();
}

internal sealed class RecipientDomainResolver : IRecipientDomainResolver
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        UseCache = true,
        Timeout = TimeSpan.FromSeconds(4),
        Retries = 1
    });

    public async Task<RecipientDomainResult> ResolveAsync(string domain, CancellationToken ct)
    {
        if (domain.StartsWith('[') && domain.EndsWith(']'))
        {
            var literal = domain[1..^1];
            if (literal.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase)) literal = literal[5..];
            return IPAddress.TryParse(literal, out _)
                ? new(RecipientDomainState.AcceptsMail, [literal], "The address uses a valid IP literal.")
                : new(RecipientDomainState.RejectsMail, [], "The address contains an invalid IP literal.");
        }

        try
        {
            var response = await _client.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            var records = response.Answers.MxRecords()
                .OrderBy(record => record.Preference)
                .ToList();

            if (records.Count > 0)
            {
                if (records.Count == 1
                    && string.IsNullOrEmpty(records[0].Exchange.Value.TrimEnd('.')))
                    return new(RecipientDomainState.RejectsMail, [],
                        $"{domain} explicitly declares that it does not accept email (Null MX).");

                var hosts = records
                    .Select(record => record.Exchange.Value.TrimEnd('.'))
                    .Where(host => !string.IsNullOrWhiteSpace(host))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (hosts.Length > 0)
                    return new(RecipientDomainState.AcceptsMail, hosts,
                        $"The mail domain {domain} has a valid MX record.");
            }
        }
        catch (DnsResponseException ex)
        {
            LogService.Debug($"Recipient MX lookup for {domain}: {ex.Message}");
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, ct);
            if (addresses.Length > 0)
                return new(RecipientDomainState.AcceptsMail, [domain],
                    $"The domain {domain} has no MX record but has a valid SMTP fallback address.");
        }
        catch (SocketException)
        {
            return new(RecipientDomainState.RejectsMail, [],
                $"The mail domain {domain} does not exist or has no usable mail destination.");
        }

        return new(RecipientDomainState.Inconclusive, [],
            $"The mail domain {domain} could not be verified.");
    }
}

internal sealed partial class SmtpRecipientProbe : IRecipientSmtpProbe
{
    private sealed record SmtpReply(int Code, string Text);

    public async Task<RecipientSmtpResult> ProbeAsync(
        IReadOnlyList<string> mailHosts,
        string address,
        string? senderAddress,
        CancellationToken ct)
    {
        if (address.Any(character => character > 127))
            return new(RecipientSmtpOutcome.Inconclusive,
                "Internationalized mailbox probing is not supported; no SMTP connection was attempted.");

        Exception? lastError = null;
        foreach (var host in mailHosts)
        {
            try
            {
                var result = await ProbeHostAsync(host, address, senderAddress, ct);
                if (result.Outcome != RecipientSmtpOutcome.Inconclusive) return result;
                lastError = new InvalidOperationException(result.Detail);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        return new(RecipientSmtpOutcome.Inconclusive,
            lastError is null
                ? "No destination SMTP server was available."
                : $"The destination SMTP server could not verify the recipient: {lastError.Message}");
    }

    private static async Task<RecipientSmtpResult> ProbeHostAsync(
        string host,
        string address,
        string? senderAddress,
        CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, 25, ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        var greeting = await ReadReplyAsync(reader, ct);
        if (greeting.Code != 220)
            return new(RecipientSmtpOutcome.Inconclusive,
                $"{host} refused the SMTP connection ({greeting.Code}).");

        var helloName = Dns.GetHostName();
        var hello = await SendAsync(writer, reader, $"EHLO {helloName}", ct);
        if (hello.Code is < 200 or >= 400)
            hello = await SendAsync(writer, reader, $"HELO {helloName}", ct);
        if (hello.Code is < 200 or >= 400)
            return new(RecipientSmtpOutcome.Inconclusive,
                $"{host} did not accept the SMTP greeting ({hello.Code}).");

        var reversePath = EmailAddressValidator.TryNormalize(senderAddress, out var sender)
            && sender.All(character => character <= 127)
                ? $"<{sender}>"
                : "<>";
        var mailFrom = await SendAsync(writer, reader, $"MAIL FROM:{reversePath}", ct);
        if (mailFrom.Code is < 200 or >= 300)
            return new(RecipientSmtpOutcome.Inconclusive,
                $"{host} would not accept a probe envelope ({mailFrom.Code}).");

        var recipient = await SendAsync(writer, reader, $"RCPT TO:<{address}>", ct);

        // Close the SMTP transaction cleanly. Cleanup is deliberately bounded and independent of
        // the main timeout: no message body or DATA command is ever sent.
        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await SendAsync(writer, reader, "RSET", cleanup.Token); } catch { }
            try { await SendAsync(writer, reader, "QUIT", cleanup.Token); } catch { }
        }

        return ClassifyRecipientReply(host, recipient.Code, recipient.Text);
    }

    internal static RecipientSmtpResult ClassifyRecipientReply(string host, int code, string text)
    {
        if (code is 250 or 251)
            return new(RecipientSmtpOutcome.Accepted,
                $"{host} accepted this recipient during the SMTP check.");

        var lower = text.ToLowerInvariant();
        var permanentlyUnknown = code is 550 or 551 or 553
            && (PermanentMailboxStatusRegex().IsMatch(text)
                || lower.Contains("user unknown", StringComparison.Ordinal)
                || lower.Contains("unknown user", StringComparison.Ordinal)
                || lower.Contains("no such user", StringComparison.Ordinal)
                || lower.Contains("unknown recipient", StringComparison.Ordinal)
                || lower.Contains("mailbox does not exist", StringComparison.Ordinal));
        if (permanentlyUnknown)
            return new(RecipientSmtpOutcome.Rejected,
                $"{host} reports that the mailbox does not exist ({code}).");

        return new(RecipientSmtpOutcome.Inconclusive,
            $"{host} returned SMTP {code}; this does not prove whether the mailbox exists.");
    }

    private static async Task<SmtpReply> SendAsync(
        StreamWriter writer,
        StreamReader reader,
        string command,
        CancellationToken ct)
    {
        await writer.WriteLineAsync(command.AsMemory(), ct);
        return await ReadReplyAsync(reader, ct);
    }

    private static async Task<SmtpReply> ReadReplyAsync(StreamReader reader, CancellationToken ct)
    {
        var first = await reader.ReadLineAsync(ct)
            ?? throw new IOException("The SMTP server closed the connection.");
        if (first.Length < 3 || !int.TryParse(first[..3], out var code))
            throw new IOException("The SMTP server returned an invalid response.");

        var text = new StringBuilder(first);
        if (first.Length > 3 && first[3] == '-')
        {
            var terminator = code.ToString(CultureInfo.InvariantCulture) + " ";
            while (true)
            {
                var line = await reader.ReadLineAsync(ct)
                    ?? throw new IOException("The SMTP server closed a multiline response.");
                text.Append(' ').Append(line);
                if (line.StartsWith(terminator, StringComparison.Ordinal)) break;
            }
        }
        return new(code, text.ToString());
    }

    [GeneratedRegex(@"(?:^|\s)5\.1\.(?:1|3|10)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PermanentMailboxStatusRegex();
}
