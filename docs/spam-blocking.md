# Explicit SPAM blocks

EQM does not infer spam from content or train a classifier. A user action creates an
ordinary persisted MailRule carrying IsSpamFilter and SpamBlockDomain. Blocks are
global, incoming-only and run before normal rules. A failed block action never falls
through to another rule. Address/domain matches use the parsed mailbox, not display
names or substrings; domain matching is exact, not inclusive of subdomains.

Shared mailbox providers are protected by SpamPolicy both in the UI and on save and
execution. The protection list is deliberately maintained in code, with regional
variants for common providers. The rules editor has a separate SPAM blocks view.
Block criteria cannot be edited there as ordinary substring conditions; delete a block
or use Not SPAM on a message to remove the corresponding block(s).

The preview offers Block / SPAM for a first observed sender, once per address/profile.
Existing messages in any cached folder prevent the first-contact proposal. This is
an invitation to make a choice, not a security warning. Maintain subscription suppresses
the proposal. The message context menu remains available for known senders.

## Classification and recovery

SpamMessageState stores classification independently of flags, Status and folder.
Account + InternetMessageId preserves classification across remote UID changes and
label copies. Local folder/UID identity is used if the InternetMessageId is absent;
a database trigger follows local moves. Existing Junk mail is never classified merely
because of its location. No original message date is changed.

The is:spam query searches classifications in the normal F3 engine. The version-2 built-in
saved-view migration adds SPAM Messages globally, without re-creating older views the
user deleted. A classified message has a SPAM / Not SPAM action in the preview.

Not SPAM confirms all matching address/domain blocks (including disabled ones), moves
the selected message back to its account Inbox, clears its classification and removes
those blocks. Other historical messages are not bulk moved or reclassified.
Remote move, local cache update and rules.json are not a distributed transaction;
failures are reported and the block is retained until the move succeeds.

## Deletion

Junk → Delete only EQM SPAM always confirms the candidate count. Other Junk messages
are excluded. The separate Empty Junk action still means delete all its contents.

SpamRetentionDays defaults to 30; 0 disables retention. The existing hourly
maintenance timer checks classified messages in each account's actual Junk folder.
SpamJunkAge starts on observation in Junk, not at the original message date.
Local moves/deletion reset the age. Delayed sync or a remote UID change may extend
retention; this is preferable to premature deletion. At most 100 expired candidates
per account are handled per pass. SPAM purge is skipped while busy, offline, focused,
shutting down or in backup maintenance.

Before deletion, the actual server envelope is re-read and its classification checked.
IMAP Junk deletion requires UIDPLUS and uses UID-scoped expunge; without it the
operation fails before setting deletion flags, never globally expunging unrelated mail.
Remote messages without InternetMessageId are not eligible for this selective deletion:
a reused UID alone is insufficient proof. Their classification cannot reliably follow
a server-assigned UID change without that identity. They can still be managed manually.
Local messages do not have this limitation.

Trash operations no longer fall back to Junk if Trash is absent. POP3 notification
batches exclude explicitly blocked senders; download statistics remain unchanged.

All development tests use temporary profiles. No production mailbox or database was
modified as part of implementation. No new release package is produced by these changes.
