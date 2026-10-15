# Eudora QuickMail 0.8.85

This release brings together the changes developed since public release 0.8.83,
including the features previously available only in local 0.8.84 test builds.

## Mail productivity

- Favorite folders provide shortcuts without moving or copying messages.
- Follow up schedules a self-addressed reminder with a link to the original message.
  A detected, correctly threaded reply cancels the reminder. Network/authentication
  failures postpone the check rather than assuming no reply exists.
- Send repeatedly schedules recurring messages at 08:00 local time with start date,
  interval in days and inclusive end date. Only the next occurrence is queued.
- Outgoing attachment checks prevent sending with missing files or unresolved embedded images.
- Private Notes can be edited beneath Subject, sorted in the message list and searched
  globally or with `N:text`. Notes are never transmitted as message content.
- Recipient privacy warnings offer to put the sender in TO and visible recipients in BCC
  when more than three distinct TO/CC addresses span multiple domains. This is a heuristic;
  the user can keep the original recipients or cancel sending.

## Rules and investigation

- Forward and Reply template rule actions use the durable Scheduled queue and loop/
  duplicate protections. Rules can have an inclusive expiration date.
- Enhanced Test Rule / Preview shows matching counts, examples and intended actions
  without executing them.
- Analyze rule conflicts lists matching active rules in execution order, competing
  actions and later rules blocked by an earlier move/delete/SPAM rule.
- Activity records local message operations for diagnosis. It does not reconstruct
  old events or certify server delivery. Retention is 90 days / 100,000 events per profile.
- Export selected messages with EML files, native attachments and a navigable HTML index.
  Incomplete exports are explicitly identified.

## Attachments

- Tools > Attachment explorer searches cached attachment metadata in the current folder
  or Everywhere, supports sortable columns, multi-save and Go to message.
- Locate missing attachment searches a chosen backup directory, reports name/size evidence
  and verifies a cached SHA-256 when available. After confirmation, recover a new copy or
  update only the selected attachment reference. Existing files and backups are not overwritten.
- The explorer includes legacy local attachment paths but excludes embedded images and
  messages whose attachment metadata has not been downloaded. It displays up to 10,000
  matches and asks for a narrower search when capped.

## Reliability and packaging

- Editable combo arrow handling and rule-dialog layout improvements.
- Unnecessary remote-style prefetch is avoided for authoritative POP/local messages.
- Unattended UI probes no longer launch the initial attachment-index modal notice.
- Updated cryptography dependency and backup coverage for the rule-forwarding ledger.
- Official Windows x64 and ARM64 builds, complete portable ZIPs, installers and update feeds.
  Portable ZIPs include the companion backup application; prefer them over copying only the EXE.

Notes and investigation data are local. Before updating an important mail archive, keep a
current backup. No bulk recovery, automatic rule rewriting or automatic attachment relinking
is performed by these new tools.

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://DinamicaTech.github.io/QuickMail/reporting-issues.html).
