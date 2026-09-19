using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Aggregates per-backend <see cref="IChangeNotifier"/> implementations behind one surface, the way
/// <see cref="MailServiceRouter"/> does for <see cref="IMailService"/>. <see cref="StartWatchers"/>
/// fans the account list out to every notifier; each notifier watches only the accounts it owns
/// (IMAP IDLE filters to <see cref="BackendKind.ImapSmtp"/>, Graph delta-poll to
/// <see cref="BackendKind.MicrosoftGraph"/>). Events from every backend are forwarded to a single
/// set of subscribers.
///
/// The IMAP notifier is <see cref="ImapMailService"/> itself — IMAP's IDLE watcher is bound to the
/// IMAP connection lifecycle, so it is not a separate object — while the Graph notifier is a
/// standalone <see cref="GraphChangeNotifier"/> (it owns its own polling loop and HTTP client).
/// Each notifier is owned and disposed by the composition root; this router only stops watchers and
/// unsubscribes on <see cref="Dispose"/>.
/// </summary>
public sealed class ChangeNotifierRouter : IChangeNotifier, IDisposable
{
    private readonly IReadOnlyList<IChangeNotifier> _notifiers;
    private readonly MailActivityPolicyService? _activity;
    private IReadOnlyList<AccountModel> _accounts = [];
    private CancellationToken _watchToken;

    public ChangeNotifierRouter(IEnumerable<IChangeNotifier> notifiers,
        MailActivityPolicyService? activity = null)
    {
        _notifiers = notifiers.ToList();
        _activity = activity;
        if (_activity != null) _activity.Changed += OnActivityChanged;
        foreach (var n in _notifiers)
        {
            n.InboxNewMailDetected += OnInboxNewMail;
            n.InboxMessagesRemoved += OnInboxMessagesRemoved;
            n.AccountReachabilityChanged += OnReachabilityChanged;
        }
    }

    public event Action<Guid>? InboxNewMailDetected;
    public event Action<Guid, IReadOnlyList<string>>? InboxMessagesRemoved;
    public event Action<Guid, bool>? AccountReachabilityChanged;

    private void OnInboxNewMail(Guid accountId) => InboxNewMailDetected?.Invoke(accountId);
    private void OnInboxMessagesRemoved(Guid accountId, IReadOnlyList<string> ids) => InboxMessagesRemoved?.Invoke(accountId, ids);
    private void OnReachabilityChanged(Guid accountId, bool isReachable) => AccountReachabilityChanged?.Invoke(accountId, isReachable);

    public void StartWatchers(IReadOnlyList<AccountModel> accounts, CancellationToken ct = default)
    {
        _accounts = accounts.ToList();
        _watchToken = ct;
        if (_activity is { CanReceive: false }) return;
        // Fan the full list out to each notifier; every notifier filters to the accounts it owns.
        foreach (var n in _notifiers)
            n.StartWatchers(accounts, ct);
    }

    public void StopWatchers()
    {
        foreach (var n in _notifiers)
            n.StopWatchers();
    }

    private void OnActivityChanged()
    {
        StopWatchers();
        if (_activity?.CanReceive == true && _accounts.Count > 0 && !_watchToken.IsCancellationRequested)
            foreach (var notifier in _notifiers) notifier.StartWatchers(_accounts, _watchToken);
    }

    public void Dispose()
    {
        if (_activity != null) _activity.Changed -= OnActivityChanged;
        foreach (var n in _notifiers)
        {
            n.StopWatchers(); // tear down watcher tasks before severing the event chain
            n.InboxNewMailDetected -= OnInboxNewMail;
            n.InboxMessagesRemoved -= OnInboxMessagesRemoved;
            n.AccountReachabilityChanged -= OnReachabilityChanged;
        }
    }
}
