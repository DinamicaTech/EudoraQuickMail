# Eudora QuickMail — AI handoff

> Living specification and technical handoff for continuing the project without the
> original conversation history.

- Last reviewed: 2026-09-07
- Code snapshot audited: `codex/local-first-fork` at `3b2a1f6`
- Fork base: upstream QuickMail commit `deb82e88aa80cdca9e2f087a345c727c9b3ab9f9`
- Current product version: `0.8.70`

## 1. Purpose

### Local OAuth publishing (2026-09-28)

`scripts/Local-OAuthBuild.ps1` imports the existing official Desktop client JSON into
an external DPAPI CurrentUser store and publishes with a private temporary source via
`GoogleOAuthCredentialsFile`. See `docs/local-oauth-builds.md`. This workflow rejects
missing/invalid credentials before building and requires an explicit numeric version.
It does not change CI credentials, application tokens, production data or GitHub releases.
The official JSON still needs to be supplied once by its owner; no actual credentials
were available during development. Tests use synthetic credentials only.

### Explicit SPAM blocks (2026-09-28, not yet released)

See docs/spam-blocking.md for the accepted blocking, recovery and retention contract.
SPAM blocks reuse MailRule with IsSpamFilter/SpamBlockDomain and exact sender matching;
shared provider domains are protected. Classification is separate from user flags.
The is:spam global saved view and Junk-only selective deletion never infer that all
existing Junk mail is SPAM. Retention defaults to 30 days (0 disables).
No production database or live mailbox was changed while implementing this feature.

### Backup agent development (2026-09-27)

`Tools/DinamicaBackup.Core`, `Tools/DinamicaBackupAgent` and `Tools/DinamicaBackup.Tests`
implement the reusable backup/maintenance agent. See
[`docs/backup-agent-implementation.md`](docs/backup-agent-implementation.md) for the
accepted product contract, on-disk formats, integration protocol and validation scope.
EQM exposes **Tools → Backup and Restore**. Publishing EQM also publishes the agent into
`BackupAgent/`; copying only the main EXE will not install the new agent. All schedules
default to disabled and task registration is an explicit configuration action.
No production backup tasks or production data were changed during this development.

This is the canonical briefing for a future developer or AI agent. It records the product
intent, everything added to original QuickMail, behavior that must survive refactors,
architecture, persistent data, migrations, build/release workflow, exclusions and the safe
procedure for resuming development.

Read this file before changing the application, then read `FORK_CHANGELOG.md`, relevant
source and the current Git diff. Source is authoritative if a later commit disagrees;
update this document in the same commit when changing a documented contract.

Never put passwords, OAuth secrets, API keys, tokens, production mail or user-specific data
in this file or Git.

## 2. Product identity and objective

**Eudora QuickMail** is a Windows desktop mail client derived from open-source QuickMail.
Its primary use is a fast, local-first Eudora replacement holding and searching several
hundred thousand messages while preserving a folder-oriented workflow.

Core requirements:

1. Messages are stored locally and remain usable without a server connection.
2. POP3/SMTP is primary. Delete a POP3 item from the server only after durable local save.
3. Multiple accounts can share one visible folder tree.
4. Migrate Eudora mail, folders, accounts, filters, headers and attachment references with
   high fidelity.
5. Search/navigation must remain usable with roughly 600,000 messages.
6. UI language is English; Windows regional date/time/number formatting is respected.
7. Remain publishable as open source; avoid commercial runtime/annual licence dependencies.

Optional IMAP is retained for community compatibility but is not the storage authority.
Outlook PST import is deliberately deferred.

## 3. Repository and environment

### 3.1 Canonical paths

- Source: `C:\Docs\Codigo\QuickMail`
- Typical production application: `C:\Docs\QuickMail`
- Typical production data: `C:\Docs\QuickMail\Data`
- Publish output: `C:\Docs\Codigo\QuickMail\publish`
- Installer output: `C:\Docs\Codigo\QuickMail\installer\Output`

The copy under `C:\Users\...` is not the project and is not backed up. Never edit it.
Before work, run `git rev-parse --show-toplevel` and require
`C:/Docs/Codigo/QuickMail`.

### 3.2 Technology

- .NET 8 WPF, Windows x64.
- SQLite/EF Core plus FTS for local search.
- WebView2 for preview, HTML composition and calendar.
- Self-hosted HugeRTE for rich HTML composition.
- MailKit/MimeKit for transport/MIME.
- Inno Setup for the autonomous installer; Velopack remains as the original update path.
- Windows DPAPI/Credential Manager for secrets.
- Microsoft 365 blue WPF styling inspired by Krypton; no wholesale WinForms conversion.

### 3.3 Primary code map

| Path | Responsibility |
| --- | --- |
| `QuickMail/QuickMail.csproj` | Main application/product metadata |
| `QuickMail/App.xaml.cs` | Startup, splash, single instance and shutdown |
| `QuickMail/Services/LocalStoreService*.cs` | SQLite, messages, folders, queries, search |
| `QuickMail/Services/LocalFolderTreeMigrationService.cs` | Canonical tree migration |
| `QuickMail/Models/ConfigModel.cs` | Global settings/defaults |
| `QuickMail/Models/AccountModel.cs` | Accounts/transports/root binding |
| `QuickMail/Models/MailRule.cs` | Rule model/semantics |
| `QuickMail/Models/QuickSearchQuery.cs` | Quick-search model/parser |
| `QuickMail/Views/MainWindow*` | Shell, tree, grid, preview and commands |
| `QuickMail/Views/ComposeWindow*` | Compose and stored-message edit |
| `Tools/EudoraImporter` | Bulk/selective Eudora migration |
| `QuickMail.Tests`, `QuickMail.IntegrationTests` | Regression suites |
| `installer/quickmail.iss` | Standalone installer |
| `build.bat` | Publish/packaging entry points |
| `FORK_CHANGELOG.md` | Curated fork implementation history |

## 4. Functionality added to original QuickMail

### 4.1 Local-first mail and transport

- Local-only SMTP send plus safe, destructive POP3 retrieval.
- Multi-account operation, Active and `Check incoming email` flags. Account management can duplicate
  a normal account's technical/folder configuration into a fresh editable account, but must clear
  username/login, passwords, OAuth/calendar identity, shared-mailbox linkage and default status.
- Optional IMAP/manual synchronization integrated into the local model/search.
- DPAPI-protected passwords and actionable connection/transport diagnostics.
- Shared account roots and one canonical visible folder tree.
- Draft, Scheduled, Snooze, Sent/Out, Trash and Junk system folders; scheduled delivery.
- Persistent Offline mode pauses send/receive, while timed Focus mode pauses receiving only;
  Check Mail restores Online mode explicitly.
- Fast bulk read/unread, flag, move, Trash and permanent-delete operations.

### 4.2 Eudora migration

- First-run and manual wizard using the same import pipeline.
- Bulk `eudora.exe` and selective multi-`.mbx`/`.fol` import.
- Folder hierarchy, messages, text/HTML, dates, raw headers and attachment links.
- Robust malformed mbox, `Content-Length`, `<x-html>` and inline-image parsing.
- Accounts/personas, Dominant default, decoded/re-encrypted passwords and optional legacy
  Check Mail settings.
- Supported `filters.pce` From/To/Subject/Body contains-and-move filters.
- Keep/copy/move referenced attachments, progress, verbose finalization and restart.

### 4.3 Search and indexing

- Indexed simple/advanced From, To, Cc, Subject, Body and date search.
- Prefix language with comparisons, AND/OR, quotes and `?` wildcard.
- Attachment count/name/content search over Office/PDF/text/archive formats, no OCR.
- SHA-256 extraction reuse, repair/rebuild tools and detailed timing log.
- Persistent last-ten quick-search history.
- Explicit Search Everywhere action over all active non-shared accounts, independent of selection.
- Saving a view while a quick search is active retains the query and folder/global scope; selecting
  that view reruns it as a smart folder. **Save as Virtual Folder…** in the active search bar is the
  explicit opt-in entry point; ad-hoc searches never create views automatically. Existing views
  without a query remain unchanged.
- `Tools > Analyze Folder`: top 20 sender domains and double-click filtered navigation.

### 4.4 Folders and messages

- Canonical physical-looking tree, recursive root/folder views and stable node IDs.
- Create/rename (`F2`)/move/drag/merge folders; update rule targets after path changes.
- Recursive unread/total badges with `K` formatting and manual recalculation.
- Persist incoming/outgoing direction independently from current folder.
- Virtualized 2,000-row pages with total-range scrollbar, dynamic loading and persistent
  sort/column/preview layout per screen resolution.
- Detached second-monitor preview, source/browser view, link-domain warning and richer
  attachment actions.
- HTTP unsubscribe links are detected from `List-Unsubscribe` and message content. The reading pane
  opens them only after showing the exact URL, in an ephemeral WebView2 profile with permissions,
  downloads, autofill and password saving disabled. Maintain subscription suppresses the controls
  persistently for that exact sender address.
- Message context export supports one or many messages as EML/HTML and copies original raw headers;
  EML reconstruction includes attachments that can be materialized locally or downloaded. Export
  and View Source are available from both the message list and reading-pane contextual menus.
- Restricted local Subject/body edit, Send Again, Reply/Reply All/Forward.

### 4.5 Rules and rapid classification

- Global/account rules, automatic incoming versus manual modes.
- From, To, optional Cc/Bcc, Subject, Body and attachment conditions.
- Move/read/unread/delete and move-plus-mark-read actions.
- Compatible-rule view, rule-definition search, cleanup and apply-to-folder.
- Read-only rule preview uses the real matcher over current-folder scope (plus Out when requested),
  reports action/counts and samples up to 20 matches before any mutation.
- Keyboard/drag quick-filter workflows and optional manual Out-mailbox classification.
- Optional background destination tab showing where the last filtered message went.

### 4.6 Compose and language tools

- HTML default; docked-tab or floating compose, with docked preferred.
- HugeRTE formatting, links, lists, colours, fonts, charmap, emoji, preview and word count.
- Editable HTML reply/forward quote, account HTML signatures and recipient autocomplete.
- Recipient chips validate syntax and mail-domain DNS automatically: invalid addresses are red,
  unresolved mailboxes stay yellow and previously confirmed recipients are green. Clicking a
  yellow chip explicitly runs one conservative SMTP `RCPT TO` probe without `DATA`; definitive
  results persist per profile, ambiguous/policy/temporary replies remain yellow for 24 hours,
  probes are serialized and limited to ten per hour, and Offline mode performs no network check.
- Draft autosave/recovery, newline-preserving templates and scheduled delivery. Compose uses a
  split Send control: ordinary Send is the default; its menu exposes Send later, Save as Draft and
  Send immediately, which explicitly bypasses the configured Undo Send delay.
- Spanish/Catalan/English spelling and one-shot language detection.
- Send and Send Later warn when the newly written text mentions an attachment in English, Spanish
  or Catalan but no file is present; quoted message history is ignored to avoid false positives.
- Selected-text Argos/DeepL translation and local LanguageTool grammar checking.

### 4.7 Calendar, shell and maintenance

- Google Calendar OAuth, FullCalendar views, day sync and direct event actions.
- Incoming ICS recognition for POP3/IMAP/Graph, with RSVP cards for meeting requests and explicit
  Add to Calendar for standalone publications; the original `.ics` remains an attachment.
- Compact Today agenda with calendar selector and green appointment pills.
- Separate taskbar notification/tray-icon settings and custom received icon.
- Early splash, single-instance recovery, shutdown hardening and structured performance log.
- Search/index/count maintenance, orphan attachment manager and safe embedded de-duplication.
- Tools > Account Health gives a non-interactive snapshot of server configuration, credential or
  silent OAuth availability, last session sync and Scheduled queue/error state for all accounts;
  columns fit the viewport and accounts needing attention use the error background.
- Windows default-mail registration candidate and installer guidance.

## 5. Non-negotiable invariants

1. **Persist before POP3 delete.** Local failure leaves the server message untouched.
2. **Direction is persistent data.** Moving sent mail out of Out never makes it incoming.
3. **Visible folders use stable IDs.** Rename/move paths without changing message identity.
4. **Account deletion never silently cascades mail.** Prefer deactivation; destructive mail
   removal requires explicit repeated confirmation.
5. **Draft/Scheduled are not materialized mail for root search.** Root includes Trash but
   excludes Draft and Scheduled.
6. **Retain raw source when available** for headers, rendering, diagnostics and reparsing.
7. **Do not auto-delete physical attachments with a message.** Shared/external references
   require later orphan maintenance.
8. **DPAPI portability is not required.** Recreating accounts on another PC is acceptable.
9. **Large work shows progress before it starts.** No unexplained UI freeze.
10. **Plain D&D only moves.** Modifier-assisted drags have separate documented semantics.
11. **UI text stays English; regional values follow Windows.**
12. **Folder paths have no padded segments.** Trim each newly created/imported segment; startup
    repairs legacy local paths and their references, but never renames an existing IMAP alias.

## 6. Persistent data and canonical folders

### 6.1 Profile

Default profile is `%APPDATA%\QuickMail`; override with `--profileDir`. The standalone
installer writes `QuickMailDataPath.txt` beside the EXE so direct launch resolves the
chosen data directory. Program/data may coincide or nest outside `Program Files`; data
anywhere under `Program Files` is rejected because normal users need write access.

| Artifact | Contents |
| --- | --- |
| `mail.db` plus WAL/SHM | Messages, summaries, mappings, FTS, attachment index |
| `accounts.json` | Account configuration, no plaintext secrets |
| `config.ini` | Global settings |
| `rules.json` | Rules/stable targets |
| `contacts.json`, `groups.json` | Address book |
| `templates.json` | Compose templates |
| `views.json`, `folderviews.json` | View settings |
| `quickmail.log`, `performance.log` | Functional and timing diagnostics |
| `mail-activity.json` | Persistent Online/Offline/Focus state and Focus expiry |
| `unsubscribe-preferences.json` | Sender addresses whose subscription controls are suppressed |
| `eudora-import.log` | Import detail/errors |
| `Attachments/...` | Materialized received/embedded/migrated files |

Never commit production databases, personal logs, credentials or attachment trees. Large
local test data must not be deleted without explicit approval.

### 6.2 Canonical tree architecture

The visible tree is `LocalFolderNode_shadow` plus `LocalFolderBinding_shadow`. Existing
message rows intentionally keep legacy `(account_id, folder_name)` ownership. A stable
visible node binds one or more account/folder sources.

This non-destructive design avoids rewriting hundreds of thousands of messages. Do not
“simplify” it without a measured migration, rollback and integrity tests. Startup converts
a legacy profile with data but no shadow tree and validates every source is mapped exactly
once.

At startup, local POP/archive bindings are normalized segment by segment. The repair moves or
merges the physical folder transactionally, updates message/detail/FTS/attachment/calendar keys,
and rewrites rules, startup/recent destinations and saved views. It is idempotent. IMAP/Graph
physical names remain server-owned aliases; only newly created remote names are trimmed. Canonical
bindings are also evidence that an active account belongs in a root view when an older
`accounts.json` is missing `FolderTreeRootId`.

System folders sort first: **In, Draft, Scheduled, Snooze, Out/Sent, Trash, Junk**. Root-level
`Inbox`/`INBOX` bind into visible `In`. `_In`, `_Out`, `_Trash` are ordinary historical
folders, not hidden aliases.

## 7. Accounts and synchronization

### 7.1 POP3/SMTP

- Check Mail processes only active accounts with `Check incoming email` enabled.
- Status identifies account and message progress.
- Receive means MIME parsed, rows committed and local visibility established before server
  deletion is attempted.
- Sent mail is local outgoing mail with delivery status.
- Normal **Send** obeys `DelaySendingMessagesSeconds` (0–600; default 30). Zero bypasses Scheduled
  and calls the transport directly. A positive value first persists the complete message in the
  same durable Scheduled outbox used by Send Later, then closes the editor and displays a
  highlighted `Undo · subject` action beside Forward until the delay expires. Undo and dispatcher
  claim the item under the same queue lock: Undo either wins and restores the latest content to
  Draft, or SMTP has already claimed it and cancellation is rejected. Dismissing the banner only
  hides it. Once claimed, the banner disappears and cancellation is not offered. SMTP, Sent-copy save,
  replied-marker persistence and source-draft cleanup run in the background; another message can be
  queued while transport is busy. The queue records SMTP acceptance before housekeeping so a crash
  cannot submit an already accepted message twice on restart.
- A failed send remains visible in Scheduled with its error/attempt state; transient failures use
  bounded backoff, while credential/OAuth/certificate/configuration failures wait for an explicit
  retry. It never becomes Draft merely because the Internet connection was down.
- Successful normal and scheduled sends refresh the real remote/local Sent source immediately.
  `ExcludeFromAllMail` is only a view flag and must never suppress synchronization.
- Errors name account/server and likely remedies: password, certificate, protocol or port.

### 7.2 IMAP

IMAP is optional, local-cache oriented and must not be the architectural authority. Manual
sync downloads/refreshes into the same model with detailed progress. IMAP messages must be
included in local search and canonical In. Do not run opaque long operations on the UI
thread; background continuations must respect shutdown/cancellation disposal.

Summary sync intentionally remains cheap (`Envelope`, flags and preview). After synchronization,
`ImapBodyBackfillService` downloads every locally cached IMAP body still missing from
`MessageDetail` through the background IMAP lease and without setting `Seen`. Its durable
`ImapBodyCacheState` rows distinguish Pending, Downloaded, NoTextBody and Error; interrupted work
resumes next start and errors retry after a delay. Copies sharing an RFC Message-ID (notably Gmail
labels) are fetched once and the body is propagated to each physical cache row, preserving unread
state. Progress is visible in the main status field.

### 7.3 Shared roots

Accounts have Active/default flags, transport, signature, incoming-check flag and
`FolderTreeRootId`/display assignment. Moving an account to a shared root must reveal its
existing mail there, not orphan or duplicate it.

## 8. Eudora import contract

### 8.1 Entry points and wizard

First run asks whether to import. Manual Import uses the same pipeline. `eudora.exe` means
bulk import; one or more `.mbx`/`.fol` selections mean selective import and target-node
selection with ad-hoc folder creation.

Inputs/defaults:

- Eudora installation/mailbox and QuickMail data folder;
- visible root name, default `Eudora`;
- Import `filters.pce`, enabled by default;
- Respect legacy Check Mail, disabled by default with safety tooltip;
- attachments Keep, Copy, Move or Cancel.

Migrated first-run presets: Accounts/Combined Views off; Today Agenda/Calendar on; new-mail
notification on; HTML compose; startup In; draft autosave 30 seconds. Preserve this explicit
wizard preset even if general defaults differ.

### 8.2 Accounts and passwords

Read `[Settings]` as Dominant and mark it default. Read `[Personalities]` and persona
sections for remaining accounts. Decode Eudora `SavePasswordText` using its documented
legacy algorithm, then immediately protect it with the normal Windows store. Never log it.
If no account has a secret, show a one-time reminder for `File > Manage Accounts`.

### 8.3 Messages and MIME

Retain imperfect source values rather than discard them: even `Softaculous <admin@>` must
remain visible. Fallback address parsing handles malformed From/To/Cc. Date fallback uses
standard headers, Eudora separators and Received metadata; never silently leave
`DateTime.MinValue` when usable date information exists.

Handle concatenated mbox/`Content-Length`, `<x-html>`, HTML/text alternatives, charset
anomalies, `Attachment Converted:`, content IDs and full raw delivery headers. Record
unparseable messages in `import_errors` plus log with reproducible source location; do not
abort the whole import.

### 8.4 Attachments

- **Keep:** reference existing Eudora `Attach*` files.
- **Copy:** copy only referenced files, organized by message year.
- **Move:** copy/verify then remove sources after a double risk warning; Eudora loses them.
- Include embedded files in copy/move accounting and later hash de-duplication.

## 9. Search and indexing

Unprefixed text searches From, To, Cc, Subject and Body recursively in selected scope. Root
uses a direct corpus query instead of a huge descendant OR expression; includes Trash and
excludes Draft/Scheduled. UI search is operator-synchronous with visible progress.

### 9.1 Quick-search grammar

| Syntax | Meaning |
| --- | --- |
| unprefixed text | From, To, Cc, Subject or Body |
| `T:`, `F:`, `C:`, `S:`, `B:` | To, From, Cc, Subject, Body |
| `A#>=1` | Attachment-count comparison |
| `AN:`, `AC:` | Attachment name/content |
| `D=2026`, `D=08/2026`, `D=29/08/2026` | Year/month/local date |
| `N` | Unread/new |
| `F` | Any flag; lone `F` is not free text |
| `I`, `O` | Incoming, outgoing |

- `;` is AND: `chocolate;D>2022`.
- Parenthesized field alternatives separated by `;` are OR: `T:(@dinamica;@google)`.
- Double quotes protect command characters/literal semicolons.
- `?` is a one-character text wildcard: `B:cho?olate`.
- Numeric/date operators are `=`, `<`, `<=`, `>`, `>=` as applicable.
- Full example: `T:(@dinamica;@google);A#=2;D=03/05/2026`.

`F3` opens/focuses search; Search button executes, Clear removes active filter. Editable
combo retains ten distinct searches and has an explicit dropdown; hide the redundant native
arrow. **Search Everywhere** reruns the same textbox query over all active non-shared accounts
without changing the selected folder; like root search it includes Trash and excludes Draft and
Scheduled.

### 9.2 Attachment index

Extract plain text/CSV/HTML/XML/JSON, DOCX/XLSX/PPTX, text PDF, and ZIP/RAR/7z/TAR/GZip
recursively with guards. No OCR. Defaults: 10 MB source, 20 MB expanded, configurable.
Store SHA-256 plus extracted text for reuse. Non-wildcard `AC:` uses FTS. If index count is
zero but attachments exist, warn on first run and show rebuild progress.

`Tools > Analyze Folder` asynchronously returns the top 20 normalized sender domains for
the recursive selected scope, clears hourglass on completion, and double-click applies
`F:@domain` to that scope.

## 10. Rules and classification

### 10.1 Defaults and matching

New rules default to All accounts, enabled, only From checked, automatic incoming off,
Move to folder, Also mark as read on, and `Also filter Out mailbox` according to its
Advanced default setting.

Checked conditions are AND. Text is partial/case-insensitive. To searches every To
recipient; `Also CC/BCC` extends that condition to Cc/Bcc. Use actual mailbox addresses,
not display aliases. Automatic rules run only when explicitly marked automatic. A manual
rule stays manual; if manually run with Also filter Out, apply it to outgoing mail even if
already classified outside Out.

### 10.2 Rules Manager

When opened for a message, show compatible rules and leave `See all filters` unchecked. If
none match, show all and check it. Search Filter matches entered rule-definition fields,
not unrelated populated fields. Caption shows the listed count.

Commands: Test selected messages, Preview current-folder effects, Save, Cancel, Run on Existing
Mail, Save and Apply to current folder, and Cleanup. Preview never writes or saves the edited rule;
it shows scanned/matched/direction/mark-read counts and the newest 20 matches. Save closes; Cancel
discards. Cleanup removes move rules with missing/undefined
stable targets and separately offers removal of imported rules pointing to In.

### 10.3 Keyboard and drag workflows

- `Shift+F`: apply compatible rules to selected messages. If the reference message has no enabled
  compatible rule, open the new-rule editor prefilled from that message instead.
- `Ctrl+Shift+F`: show/select matching filters; only create if none exists.
- `Ctrl+Shift+A`: Filter all like this—find all compatible rules and apply to current folder.
  Partition execution by physical account/folder: one unavailable remote source must not roll
  back or conceal successful sources. Refresh successful moves and report partial failures with
  the affected account; `invalid_grant` requires the Google account to be linked again. If no
  enabled compatible rule exists, open a prefilled new-rule editor just as `Shift+F` does.
- Plain message D&D: move only; never open Rules Manager.
- Shift-drop on container: ask/create/reuse subfolder, move there, offer rule creation.
- Shift-drop on leaf: move there and open a rule with that target.
- Ctrl+Shift-drop: quick filter. On a container ask/create the subfolder, then make an
  enabled All-accounts From-domain Move+mark-read rule and apply it to the source folder.
  Reuse/retarget an equivalent From+Move rule rather than duplicate it.

Empty does not automatically mean “leaf”. A newly created empty folder can be a quick-filter
container, while legacy folders can hold messages and children. Always move to the actual
created/selected target, never its parent.

After filtering/deleting, preserve viewport and select the next surviving row, not the
first. If enabled, update a non-activating `Filtered → folder` tab with the destination.

## 11. Folder-tree behavior

- Any node displays messages from itself and descendants.
- Root shows the materialized corpus using a direct efficient scope.
- System folders sort first; other siblings alphabetically.
- Create below root, rename (`F2`), move and delete refresh tree/counts immediately.
- Folder D&D confirms. If destination already has a same-name child, ask to merge, move
  messages/children, update rule targets and remove only the empty source.
- Context Move Folder uses a valid, non-over-restricted selector and the same safe logic.
- Context Mark as read confirms, acts recursively and updates ancestor counts.
- Context Filters opens Rules Manager restricted to rules targeting that folder.
- With tree focus, one typed letter selects the first matching folder; a second within
  500 ms forms a two-letter prefix. Folder selection must not immediately steal focus to grid.

## 12. Message list, preview and attachments

### 12.1 Grid

Render/page cap is 2,000 while the scrollbar represents total results and pages load
dynamically. Mouse wheel is line-oriented—the accepted behavior is about three rows per
notch—not 20+ rows and never a jump back to top.

Columns include attachment, In/Out, reply indicator, Status, From, To, Subject, Date, Time
and Folder. Date/time are regional. Status sorts by displayed text and contains delivery
error/flag/New as appropriate; replied is a separate discreet icon. Incoming background is
`#FCFCFC`, outgoing `#FCF7FF`.

Column width/order/visibility and preview height persist per screen resolution. A different
resolution uses defaults. Alt+click cell filtering stays inside canonical scope including
INBOX bindings. Delete/AltGr in search/edit fields must not delete mail or steal focus.

### 12.2 Preview

Single click previews while grid retains shortcut focus. Multiple selection suppresses
reading preview and status shows selected count. Opening preview must not hide the selected
row by changing scroll position.

Header labels Subject, From, To, Cc and Date separately. `Bla bla bla` expands raw headers.
Context also has Send to browser, Reply, Forward and Send again for outgoing mail. Detach
Preview sits top-right; detached preview follows selection and suppresses duplicate embedded
preview until closed.

Link hover writes the complete URL to left status. Prefix `Warning!` when its host/domain
differs from the incoming sender domain.

### 12.3 Existing-message edit

- Double-click Draft/Scheduled opens normal compose; Scheduled supports reschedule/send now.
- Double-click materialized received/sent mail opens restricted edit: envelope locked,
  Subject/body writable.
- Save updates body/summary/preview/FTS atomically.
- Send Again makes a new editable copy.

### 12.4 Attachments

Double-click opens; EXE/VBS/COM/BAT and similar require warning. Context includes Copy
attachment (full path) and Explore (Explorer selection); frame background is subtly
highlighted. Received files are materialized below profile, existing outgoing sources are
referenced, and Eudora files obey import strategy. Temporary materialization is cleaned
safely.

## 13. Composition and language services

### 13.1 Lifecycle

Compose opens directly as HTML, not plain then HTML. Docked tab is default; floating is
configurable. Messages tab remains first; editor captions use concise Subject only.

Header order: From, To, Subject, Cc, Bcc, Language. Tab from Subject goes to body. Changing
From replaces the old signature without deleting user text or stacking signatures. Insert
writable space/`<br>` above signature.

Save Draft saves and closes. Autosave creates/reuses Draft without resurrecting a draft the
user deliberately deleted. Send Later asks date/time and creates/reuses Scheduled. Normal Send is
also durable: it queues an immediately due Scheduled item and releases the editor before network
I/O. Recipient
autocomplete uses recently sent addresses, default two years, displayed as
`Name <address>` when known.

### 13.2 HugeRTE

Self-hosted editor tools include autolink, charmap, preview, word count, advanced lists,
visual blocks, alignment, indentation, text/background colour, font family/size, quote,
horizontal rule and emoji. Fullscreen is known to be ineffective/low value inside a docked
tab; do not destabilize the editor to force it.

### 13.3 Spelling, translation and grammar

- Spanish, Catalan and English; `(None)` disables checking.
- New mail starts None and makes one detection attempt after about five useful words, then
  does not keep changing automatically.
- Switching language reloads the right dictionary without losing text/caret.
- Catalan uses a public Softcatalà-compatible Hunspell dictionary.
- Catalan proofing preserves the caret with temporary DOM boundary markers while it rebuilds
  underlined spans; contextual suggestions replace the word atomically on the first selection.
- Context command labels stay English even if Chromium suggestions localize.
- Translation affects selected text only. Argos is local/free; DeepL optional with protected
  key, provider setup/test/models and balance query.
- Grammar affects selected text through local LanguageTool for all three languages. Show
  wait/status, multiple selection, Apply selected and Apply all.

## 14. Calendar

Google Calendar uses user-created desktop OAuth credentials; refresh tokens are in Windows
Credential Manager. Mail transport credentials and calendar authorization are independent.

Calendar is first main tab, In second—not a pseudo-folder. FullCalendar provides day/week/
month, visible Refresh (`F5`), day sync, click/double-click creation and context New/Edit/
Duplicate/Share/Delete. Share opens a new message with blank To, subject `Reserva: [event name]`
and an in-memory `.ics` attachment. Duplicate opens a new-event editor with copied content,
schedule, recurrence and calendar but a fresh event identity. If any Google calendar is linked,
use it as default new-event target, not Local.

Incoming calendar MIME handling accepts both `text/calendar` and a `.ics` filename even when the
sender labels it `application/octet-stream`. Preserve the raw body in `calendar_ics` and expose it
as a normal downloadable attachment. `METHOD:REQUEST` renders Accept/Tentative/Decline and is
harvested into the pending-invitation calendar; `METHOD:CANCEL` cancels the matching event.
`METHOD:PUBLISH` (and a standalone ICS without an invitation method) renders **Add to Calendar**,
opens a new-event editor with a fresh UID and the usual preferred target, and is not silently
harvested. `METHOD:REPLY` is informational and must never offer RSVP controls.

For POP3, extract/parse calendar text **before** materializing attachments: decoding the
`MimeContent` may consume the transport-backed stream. `LoadDetailAsync` also repairs an empty
`calendar_ics` from a materialized local `.ics` attachment (maximum 2 MB) and persists the repair;
this supports messages downloaded by the brief affected build without requiring redelivery.

Drag/drop day rescheduling is feasible but not yet implemented. It must be restricted to writable
single events, preserve duration/all-day/time-zone semantics, push the change to Google before
committing the local row, and revert the FullCalendar drag on server failure. Recurring and
read-only/shared events should remain non-draggable until their scope/permission behavior exists.

Compact Today agenda can replace Accounts and includes All/Local/specific dropdown. Empty
text is “No appointments today. Click + to schedule an appointment.” With no Google link,
offer “Click here to link Google Calendar” opening Advanced settings.

Never rebuild full calendar synchronously at startup; background/on-demand and log timing.

## 15. Notifications, status and reliability

### 15.1 Status

Three regions:

1. left ~70%: current meaningful action/error/link URL;
2. middle: selected count and last successful download;
3. right: regional current date/time.

Remove noise such as Offline/rule counts. Highlight meaningful transient work and paint it
before starting delete/filter/download/index operations.

### 15.2 Notifications

Taskbar notification and received-mail tray icon are separate settings. Use custom
`QuickMailReceived.ico`. New mail refreshes current matching mailbox/count without a second
click while preserving selection where possible.

### 15.3 Mail activity modes

**Work Offline** pauses all automatic/manual transport dispatch and all receivers; pressing Send
durably queues the message without network I/O. **Focus mode** pauses receivers until its deadline
but leaves sending enabled. Pressing Check Mail ends either mode and then performs the check.

### 15.4 Startup/shutdown

Splash appears before expensive service/SQLite initialization, with 128×128 logo and named
phase. Migrations/count construction may be slow but must be visible/logged.

Single-instance launch activates current window; if hung/unresponsive, offer to terminate
that exact process. Shutdown cancels once and tolerates disposed cancellation sources; never
show repeated `ObjectDisposedException: The CancellationTokenSource has been disposed`.

## 16. Maintenance

Tree badges show `[unread]/[all]` recursively with compact thousands, e.g. `253/4K`. Refresh
after receive, read/unread, flag/move/delete/rules, draft/scheduled save and folder click.
`Tools > Recalculate Folder Totals` repairs and refreshes.

`Tools > Delete Orphaned Attachments` scans legacy Eudora roots when Keep was used, otherwise
QuickMail attachments. It displays full path, regional date/time and KB with separators;
sorts columns; double-click opens; buttons Remove selected/Remove all/Copy list/Cancel.
Never traverse reparse points.

Embedded hash consolidation is valid only for QuickMail-controlled native/copied/moved data
and requires preview/confirmation. Never rewrite original Eudora embedded files in Keep mode.

## 17. Settings introduced by the fork

- Show Accounts panel, Combined Views, Calendar and Today Agenda.
- Default calendar source.
- Taskbar new-mail notification and separate received-mail tray icon.
- Compose open mode Docked/Floating; HTML default; draft autosave; startup folder.
- Attachment source/expanded index size limits.
- Enable logging/performance diagnostics.
- Background destination tab after filtering.
- Default Also filter Out mailbox for new rules.
- Translation/grammar/spell resources.
- Per-account Active, Check incoming email, root, invalid-certificate exceptions and HTML
  signature.

New settings require a model default, backward-compatible persistence, settings UI and a
regression test for an old profile missing the value.

## 18. Keyboard reference

| Shortcut | Action |
| --- | --- |
| `F1` | User guide |
| `F2` | Rename selected folder |
| `F3` | Open/focus quick search |
| `F5` | Refresh calendar/relevant view |
| `Ctrl+0` | Open Out |
| `Ctrl+1` | Open canonical In, not separate All Inboxes |
| `Ctrl+H` | Add compose attachment |
| `Delete` | Move selected mail to Trash |
| `Shift+Delete` | Permanently delete without confirmation |
| `Shift+F` | Apply rules to selection |
| `Ctrl+Shift+F` | Create/edit compatible filter |
| `Ctrl+Shift+A` | Filter all like this |

Focus owns keystrokes: editable-field Delete/AltGr, tree type-ahead and grid shortcuts after
preview must not interfere with each other.

## 19. Build, test and installer

Run from `C:\Docs\Codigo\QuickMail`.

### 19.1 Tests

```powershell
dotnet test QuickMail.Tests\QuickMail.Tests.csproj -c Release
dotnet test QuickMail.IntegrationTests\QuickMail.IntegrationTests.csproj -c Release
```

For import/search/folder/rule/SQL work, run focused tests then the full unit suite. Use a
copy/test profile for destructive migration or maintenance; never production `mail.db`.

### 19.2 Publish

```powershell
build.bat publish
```

This publishes self-contained win-x64 QuickMail and EudoraImporter into `publish\`.
App-only fallback:

```powershell
dotnet publish QuickMail\QuickMail.csproj -c Release -o publish\ --no-restore
```

The fallback is not a complete Eudora-import distribution unless importer and Assets are
also present.

### 19.3 Installer

```powershell
build.bat installer-production
```

Output is in `installer\Output`. Generate but do not launch automatically—the product owner
runs installation tests. Installer asks program/data directories, optional Eudora import,
then whether to register as a mail-client candidate/open Windows Default Apps. Modern Windows
does not permit silently forcing the default mail client.

Retained paths: `build.bat installer` for Velopack/update packaging and
`build.bat installer-standalone` for the standalone marker. App is self-contained for .NET,
but WebView2 Runtime is required. Code signing is not configured.

## 20. Performance and diagnostics

Time matters more than disk space: useful indexes for sortable/filter fields are preferred
over repeated scans, with write/migration impact measured. Log these candidates:

- splash/startup and SQLite open/pragmas/schema/migration phases;
- cold versus warm folder selection;
- recursive counts;
- page/count/sort SQL and grid render;
- quick/attachment search;
- POP3/IMAP connect, enumerate, download, parse, persist and delete;
- rule matching, batch move and refresh;
- folder drag/move/merge;
- calendar load/sync.

`performance.log` is controlled by Enable logging and records phase, elapsed ms and safe
counts/IDs—not bodies/passwords/tokens. `quickmail.log` records functional errors.

Cold SQLite cache can make the first large folder several seconds slower. Accept only after
logging proves I/O/cache rather than query explosion. Root queries must not build one WHERE
expression per folder.

## 21. Known limitations and deliberate exclusions

- **Outlook PST:** deferred; commercial components conflict with public/open distribution,
  and evaluated free alternatives lacked reliability.
- **OCR:** absent; image-only/scanned PDFs are not content-searchable.
- **HugeRTE fullscreen:** limited in docked tabs.
- **External HTML:** rendering depends on WebView2/network policy; retain link warning and
  never silently execute active content.
- **Secrets:** DPAPI values are not cross-user/machine portable.
- **Attachment lifetime:** message deletion does not remove physical files; use cleanup.
- **Calendar:** useful Google integration, not Outlook-level groupware.
- **IMAP:** compatibility path, not architectural authority.
- **Publication:** local history is prepared, but never push/publish without explicit approval.

## 22. Regression checklist

1. Current and clean profile launch; splash appears immediately.
2. Legacy folder conversion is idempotent and preserves/matches every source.
3. Canonical In includes local `In`, POP3 `Inbox` and IMAP `INBOX`.
4. Root click/search includes Trash, excludes Draft/Scheduled and avoids huge SQL trees.
5. Sort/columns/preview layout persist only for matching resolution.
6. POP3 persists before delete; In/count/tray refresh on arrival.
7. Accounts with Check incoming email off are skipped.
8. Search covers unprefixed, `F:`, `AC:`, `N`, `F`, `I`, `O`, date and combinations.
9. Eudora handles malformed addresses/dates, x-html, headers and attachments.
10. Plain D&D only moves; modifier D&D uses the exact child target.
11. Rule address/To/Cc/Bcc match, Move+read and manual Out behavior work.
12. Folder rename/move/merge keeps all messages/children and retargets rules.
13. Draft autosave does not resurrect deleted drafts; permanent delete stays deleted.
14. Scheduled survives restart and edits/reschedules/sends.
15. Compose opens directly HTML; account/language changes preserve body/signature correctly.
16. Link warning, browser source and attachment Copy/Explore work.
17. Linked Google calendar is default and supports direct event actions.
18. Closing during background work leaves no disposed-token dialog/orphan process.
19. Tests pass; published EXE is full self-contained output, not a small launcher.
20. Installer is generated but not launched automatically.

## 23. Safe continuation procedure for a future AI

1. Verify repo root/branch and inspect `git status`.
2. Read this file, `FORK_CHANGELOG.md`, latest Git log and supplied diagnostics.
3. Preserve unrelated dirty changes; assume they belong to the user.
4. Reproduce with a copy/test profile; diagnose production data read-only.
5. Trace complete command path: UI → service → store/transport → refresh/count.
6. Add/update regression tests before broad folder/search/rule/MIME refactors.
7. Make database migrations additive, idempotent and restartable; log progress.
8. Measure against large data through `performance.log`, not only fixtures.
9. Build only when requested. Distinguish EXE from installer and report absolute output.
10. Never launch installer unless explicitly requested.
11. Commit coherent local history with no secrets; never push/publish without authorization.
12. Update this file and `FORK_CHANGELOG.md` when a contract/capability changes.

## 24. Git, attribution and publication

The fork derives from Kelly Ford's QuickMail and must retain upstream copyright, licence and
attribution. Local base is `deb82e88aa80cdca9e2f087a345c727c9b3ab9f9`; first broad
local-first checkpoint is `bcb4972`; subsequent focused history is in `FORK_CHANGELOG.md`.

The eventual community fork name is Eudora QuickMail. Before publishing:

- create/use a fork-owned remote; do not accidentally push upstream `origin`;
- audit licences for dictionaries, HugeRTE, LanguageTool, Argos models and icons;
- exclude profiles, logs, DBs, API credentials and generated installers from history;
- refresh README/screenshots/user guide/release notes;
- choose version/tag and code-signing policy;
- run clean-machine installer and migration checklist.

## 25. Definition of successful reconstruction

Recreation from original QuickMail plus this specification succeeds only if it can:

- safely migrate a large Eudora archive into a shared canonical local tree;
- retrieve POP3 without losing server mail on local failure;
- search roughly 600,000 local messages and indexed attachments predictably;
- classify incoming/outgoing conversations together with fast folder/rule workflows;
- compose high-fidelity HTML with multilingual assistance;
- expose Google Calendar/new-mail state without blocking startup;
- survive schema/folder upgrades and shutdown while preserving data;
- install on another Windows PC with operator-selected program/data paths.

Those behaviors—not incidental class names—are the durable product contract.

## 26. In-app update follow-up (after 0.8.77)

See [in-app-updates.md](docs/in-app-updates.md) for installed/portable download, integrity checking,
drained shutdown, rollback, preserved profiles and first-adoption requirements. The previous
portable release-page-only behaviour is replaced where an official verified ZIP is available.

## 27. Stable message results (October 2026)

Quick/advanced searches retain the displayed result page in memory. Rule moves/deletes remove
only confirmed affected rows; successful deletion does not reload the mailbox. Surviving rows
retain order and selection advances to the next survivor. Read/unread reconciliation updates
properties without changing membership. Explicit search, F5, paging or reopening the view
reevaluates the query. Sorting a filtered page sorts its current rows without a new query.
Unfiltered In continues accepting arrivals in the selected order; the UI preserves the visible
top-row anchor around batched arrivals. No temporary SQLite table or schema change is required.

Snooze additionally offers 30, 90 and 180 days, and one calendar year, all at 09:00 local time.
Custom Snooze and Send Later date-only inputs default to 09:00; an explicitly supplied time,
including 00:00, is preserved. Existing future-date validation still applies.

## 28. Cursor recovery on activation (October 2026)

MainWindow.Activated invokes CursorVisibilityRecovery on the UI thread, only when that window
is the native foreground window. It probes the thread's ShowCursor counter and immediately
undoes the probe if already visible; negative counters are raised to zero (maximum 32 calls).
Windows touch/pen suppression is respected. Cursor shapes and WPF OverrideCursor are untouched.
Corrections are recorded as `Cursor recovery` in quickmail.log, including native cursor flags.
This is a defensive workaround for intermittent whole-window cursor disappearance, not a
confirmed fix of its origin (the HTML editor is suspected but unproven). Clicking an already
active window does not trigger it; switching away and back does. No timer or global hook is used.

## 29. Copy missing attachment paths

Attachment contextual Copy copies an existing stored absolute path verbatim, even when the
file is missing or its UNC share is offline. It must not download/recreate the file or replace
that path with a temporary one. Applies to the reading pane and standalone message window.
Server-only attachments without a stored absolute path retain materialization before copying.
Open, Explore and drag-and-drop still require a real file; their behavior is unchanged.

## 30. Safe startup during backup

When startup cannot acquire the profile lease, EQM shows BackupStartupWindow with safe
cancellation and a wait for BOTH the agent and profile locks. It never removes lock files,
terminates the worker or bypasses interrupted-restore markers. Active restorations cannot be
cancelled from this dialog. External backup windows require explicit acknowledgement that the
external copier has stopped; the agent cannot cancel Synology or verify its state.

New agents publish .backup-active.json (run ID, operation, PID/start time) and monitor a
run-ID-scoped .backup-cancel.json through a linked cancellation token. Existing cleanup paths
complete before reopening. Cancellation is cooperative, not necessarily immediate during an
uninterruptible system/NAS call. Old running agents cannot accept this protocol; the dialog
explains this and allows waiting/closing without forcing anything. Deploy EQM AND BackupAgent.

A live .backup-startup-waiter.json identity prevents duplicate automatic reopen. Scheduled
cancellation consumes already-due schedule occurrences (with local receipts for skipped work)
so the next five-minute task tick does not close EQM again; future occurrences remain scheduled.

## 31. Folder opening progress

SelectFolderCommand publishes a completion lifetime for all folder navigation entry points.
MainWindow uses the existing two-second delayed mini-splash, without activating it or stealing
tree/list focus. Superseding navigation or closing the owner ends the old indicator; every exit
from selection (including failure/cancellation) completes its lifetime. Startup retains its own
splash. Remote aggregate synchronization reports account/folder and source number, not a fabricated
percentage. This is feedback only: synchronization order and folder-loading behavior are unchanged.

## 32. Independent search result tabs

Display settings: `OpenSearchesInResultsTab` defaults to true, including existing profiles with
no explicit setting. F3 Search, Search Everywhere (including Ctrl+Enter), advanced search and
Alt+cell search create a closeable SearchResults tab; searches launched inside it reuse it.
Disabling the option restores inline search. Saved virtual folders retain their existing behavior.

MainViewModel.SearchTabs captures folder/account, query/scope, advanced criteria, list presentation,
raw and displayed rows, pagination, selection and status per participating list tab. MainWindow
keeps the multi-selection and scroll anchor. Switching back restores the snapshot without a query;
Escape/Clear returns to the originating tab, leaving results available, and closing results also
returns to that origin. F5 explicitly reruns the local query without enumerating remote folders.
Folder navigation leaves results intact. Result membership stays frozen on sync/read changes,
while removals propagate to inactive snapshots and ordinary inactive folders accept new mail.
Snapshots are in memory only; no database migration or persisted per-search tables are added.
Automated coverage: SearchResultsTabTests plus existing tab, local-search and folder-state tests.

## 33. Past appointments in month view

FullCalendar month view dims finished appointments using opacity and reduced saturation; account
colors and all selection/edit/context-menu behavior remain intact. Day/week/Today agenda are
unchanged. The event end (exclusive for all-day events) determines completion, not its start.
All-day events without an end use the next local midnight; timed events without a known end are
not dimmed. CSS is updated every minute and on focus/visibility/view changes without reloading
events. Hover/focus increases readability. Tests: CalendarContextMenuTests and
`node scripts/Test-CalendarPastEvents.cjs`.

## 34. Durable images inserted in outgoing HTML

The HugeRTE bridge snapshots blob images as data URIs (blob cache, with a FileReader fallback),
without replacing the live DOM/caret. Correlated capture acknowledgements are awaited before
send, scheduling, draft/autosave and stored-message save; capture failure stops the operation.
Revision numbers discard late snapshots. Editor-only data-mce image attributes/srcset are removed
from local-image snapshots so they cannot reintroduce an expired blob URL.

EmbeddedImageStore persists PNG/JPEG/GIF/WebP/BMP images under profile/Embedded/<composition-guid>/
with content-hashed files/CIDs, and rewrites snapshot HTML to cid:. The directory identity survives
folder moves and draft/sent IDs. Inline metadata is separate from the visible compose attachment
list and is retained in local Draft, Scheduled, undo-draft and Out. Reopening reuses referenced
files; no automatic deletion is performed because other copies may still reference them. Existing
broken blob-only messages are not repaired. Inline images have a 20 MB limit consistent with the
reader; larger images can be ordinary attachments. External HTTP images are not downloaded.

MimeMessageBuilder emits multipart/related with inline Content-ID resources inside the HTML
alternative. It loads durable file bytes when necessary and rejects unresolved blob content.
IMAP details retain/fetch bounded inline MIME image resources and cache them through the existing
received-resource store (Attachments/Embedded); newly authored images use the separate Embedded
root. Reply/forward preserve inline metadata. Received resources may be rendered from bytes or disk.

Native backup includes Embedded automatically for EQM jobs when attachment copying is enabled,
including existing jobs. Relative inventory paths make normal restore/restore-test cover it too.
Tests: EmbeddedImageTests, relevant compose/MIME/local-store suites, BackupTests, and
`node scripts/Test-EmbeddedSnapshot.cjs`.

## 35. Explicit Help update checks and truthful status (2026-10-10)

Help offers Check for updates now and a separate View releases on GitHub action.
Manual checks query the feed again without restarting, and open the existing in-app
update dialog when an installable update is found. The status entry also retries on click;
its tooltip contains the last successful check time or failure/disabled details.
Startup checks remain silent when up to date. Duplicate checks are prevented while pending.
Only a successful no-newer-version result reports No updates available; network, timeout,
invalid release metadata and Velopack failures are no longer converted to that result.
The quickmail-standalone-install marker is respected and explained (including its full path),
not deleted automatically. Removing it externally allows the next manual check without restart.
The existing installation's marker has not been removed by this change.
Regression coverage: UpdateHelpTests (manual retry/dialog, disabled marker, failure after
a prior available update, concurrent checks).

## 36. Favorites, send attachment checks and conditional follow-ups (0.8.84)

Folder context menus offer Add to favorites / Remove from favorites. Favorites are shortcuts
at the top of the tree, persisted as stable node keys in config.ini (FavoriteFolderKeys JSON).
They reference the original folder model: no message copies or separate mailboxes are created.
Missing targets are hidden. Dragging a shortcut cannot move the underlying folder; dropping
messages onto it still targets that folder. Noncanonical path-based keys may need re-adding
after a rename. Canonical folder IDs survive renames/moves.

Compose, scheduled dispatch, SMTP and Graph validate all attachments before sending. Retained
bytes remain valid even if the original file disappeared. Otherwise load the absolute source
path; missing/unreadable/unhydrated resources and unresolved HTML cid references stop sending
with names/paths and restore/reattach/remove instructions. A queued item remains visible with
an error and automatic retries disabled, rather than silently omitting the resource.

Compose offers Follow up (days), zero disabled. After successful transport acceptance, create
one self-addressed conditional reminder in Scheduled with subject Follow up: <original subject>,
a short excerpt and the existing quickmail original-message link. No special view is added.
Sent-message context menu Follow up permits a custom future date/time (date only -> 09:00).
Scheduled permits edit, reschedule and cancel as usual; draft headers preserve follow-up metadata.
Stable outgoing Message-ID links the MIME sent to its Sent copy; deterministic queue IDs prevent
duplicate reminder creation during accepted-transport recovery. Never schedule on send failure.

A reply from any TO recipient cancels the reminder only when In-Reply-To/References contains
the original Message-ID. Ignore self replies, automated replies and delivery reports; do not
infer replies from matching subjects. Cached replies are checked every five minutes. Before
sending a due reminder, synchronize the account Inbox (real POP3 reception for POP accounts),
then fetch missing candidate headers and recheck. Network/authentication failures postpone it;
Focus/Offline postpone until incoming checks are possible. LocalArchive cannot verify remote
replies and therefore defers. Server-side filing into an unsynchronized non-Inbox folder and
replies without threading headers remain detection limitations. Reminders require EQM running.
Metadata lives in the existing scheduled-mail.json and draft headers; no DB schema migration.

Regression coverage: ProductivityFollowUpTests plus full non-clipboard suite. Includes missing
resources, exact reply matching, automatic-reply exclusion, cancellation, offline deferral,
idempotence, metadata roundtrip and actual POP3 check failure propagation. Pkcs dependency pinned
to Microsoft security patch 8.0.2 (CVE-2026-84008 and related advisory); no unrelated upgrades.

## 37. Rule forwarding and repeated sending (2026-10-15, pending publication)

RuleAction.Forward is appended to the serialized enum (existing values unchanged). Rule Manager
offers Forward with a single ForwardTo address and requires a matching condition. The original
account sends a normal forwarded message, including regular/inline attachments and HTML/plain
content, through Scheduled's durable queue; originals remain in their folders. Also mark as read
is applied only after forwarding has been queued. Test Rule previews the forward destination.

Rule forwarding rejects configured own-account destinations and skips messages carrying
X-QuickMail-Rule-Forward or Auto-Submitted: auto-replied. Outgoing rule forwards include the
marker and Auto-Submitted: auto-generated. A durable hash ledger at scheduled-mail.json.forwarded.json
deduplicates by rule/account/source identity/destination across restart (Internet Message-ID when
available, otherwise folder+message ID). It records successful QUEUEING, not delivery: failures stay
in Scheduled for retry; cancelling a queued forward does not cause future rule runs to recreate it.
The ledger is included in native EQM backup, including preexisting job definitions. External systems
that strip markers or alias addresses can defeat loop detection; do not configure forwarding cycles.

Send repeatedly appears in the compose File menu and Send dropdown. A dialog requests start date,
whole-day interval (1–36500), and inclusive end date. The first date must still be in the future at
08:00 local time. Prefix Repetition: is added once. The chosen Windows time-zone ID is persisted,
and every occurrence is calculated at local 08:00, preserving daylight-saving rules, not +24h UTC.
Only the next occurrence is in Scheduled. After transport acceptance and ordinary Sent handling,
the existing queue record is atomically replaced by the next future occurrence with a new MIME
Message-ID. Recovery of accepted transport never resends the original. A failure before acceptance
keeps the current occurrence and creates no successor. On late startup send ONE overdue copy,
then skip missed dates on the original cadence; if already past the end, send that overdue copy
and end. Deleting the pending Scheduled message ends the series. Editing uses Send repeatedly to
change dates/interval/end; metadata also survives draft saves and reopen. Follow-up is independent.

Locally authored attachments supplied as bytes without an existing disk path are materialized in
Attachments/Local (inline in Attachments/Embedded) before saving authoritative local messages.
This keeps forwarded attachments accessible in Scheduled/Out after restart; no original is deleted.
Tests: RepeatedSendingAndForwardTests; existing rules, queue, local storage and theme suites.

## 38. Private Notes, rule previews/replies, recipient privacy, export and activity (2026-10-15, pending publication)

Private Notes are editable beneath Subject in compose and the existing-message editor. A sortable
Notes column is added to the message list. Global/F3 search includes Notes; N:word restricts search
to Notes, while the preexisting bare N status syntax is preserved. Notes are local metadata, never
MIME headers/body or reply/forward text. Draft/queue/local Sent round-trip them. A small separate
MessageNotesFts index avoids rebuilding the large body FTS; PrivateMessageNotes preserves annotations
by account + RFC Message-ID across IMAP cache refresh/labels. Both are included in the DB backup.
The compose field is explicitly unavailable without the local store (online-only mode).

Rules support inclusive local ActiveUntil date and Reply template. Expired rules remain configured
but are skipped, including explicit execution. Preview shows scanned/matching counts and examples,
including full cached body and CC/BCC evaluation; it does not execute actions. Sending previews count
condition matches, not guaranteed sends. Reply template uses plain text, existing template expansion,
and original Message-ID threading, with conservative list/bounce/auto-reply/self guards. Its durable
ledger allows one queued reply per sender/account/rule/expiration campaign. Without an expiration this
means once until that campaign changes. Disabling a rule does not cancel an already queued message.

Recipient privacy checks distinct visible TO+CC mailboxes: more than three across more than one exact
domain offers Yes=own sender in TO and all original TO/CC in BCC (keeping existing BCC, deduplicated),
No=retain visible addresses, Cancel=do not send. It is a heuristic, not proof that recipients are strangers.
Normal, immediate, scheduled and repeated composition use it; a modeless repeated-date dialog is
followed by revalidation of the final recipient/sender values. Existing unattended rules are not blocked
by interactive prompts.

Export > Selection with attachments and index creates a fresh uniquely named directory with numbered
message subfolders, full EMLs and native attachment files plus an escaped, inert HTML index. Select all
in Email History to export that displayed result set. It exports the selected rows only, not hidden or
unloaded matches. Missing bodies/attachments mark that message incomplete in the index; no silent
success and no overwriting old exports. Notes and activity are excluded. Remote reads use prefetch
(no Seen side effect) and remain available if the local detail read fails.

Activity context menu/palette reads a separate MessageActivity table. SQL triggers observe new local
catalogue rows, movements and removals; explicit entries record rules, snooze/wake and queued transport
acceptance. These are LOCAL observations, not server delivery guarantees. Identity is account + RFC
Message-ID, with account/folder/local ID fallback migrated on local moves. No historical reconstruction.
Retention: 90 days, max 100,000 events per profile; the view shows latest 500 per message. Bodies,
attachment contents and credentials are never logged. The table is part of normal DB backup.

Tests: MessageNotesTests, RecipientPrivacyTests, RuleEnhancementTests,
MessageActivityAndBundleTests and the existing repeated-sending/rules/search/compose suites.

Integration regression: POP/LocalArchive folder loads no longer launch remote-style prefetch cache
probes for already-authoritative local bodies. This also avoids a needless SQLite reader racing
local-folder disposal. Remote prefetch tasks receive their cancellation token before being scheduled.

Verification: Release app build with warnings treated as errors passed (0 warnings/errors).
The non-clipboard full run recorded 3,325 passes and 7 keyboard-handler test failures
(ComposeWindowEnterSendGuardTests and TabStripNavigationTests). A separate rerun of those
classes together with the new feature suites passed all 85 tests; the full-suite failures
are not reproducible in that isolated run and must not be reported as a clean full-suite pass.
TRX evidence: .codex-artifacts/test-results/message-tools-final.trx and message-tools-targeted.trx.
Affected UI surfaces were captured in parchment/dark; final Rule Manager layout captures are in
.codex-artifacts/message-tools-20261015-03. No standalone package or GitHub release was generated.

## 39. Attachment explorer, assisted recovery and rule conflict analysis (2026-10-15, pending publication)

Tools offers Attachment explorer and Analyze rule conflicts for the message selection. The latter
is also in the message context menu; reading-pane attachments offer Locate missing attachment.
See docs/ATTACHMENT-INVESTIGATION.md for scope, keyboard walkthrough and infrastructure details.
The explorer queries cached attachment JSON (including legacy paths) without downloading all mail,
supports folder/Everywhere scope, search, sortable columns, multi-save and Go to message. Results
are bounded to the newest 10,000; embedded resources are excluded. Online-only mode explains its
local-store requirement. No persistent DB migration is introduced.

Recovery searches only a selected directory tree, skips reparse points, supports cancellation and
reports skipped locations. Name/size evidence is labelled unverified; a known cached SHA-256 must
match and is rechecked before repair. Explicit confirmation chooses copy into Attachments/Recovered
or link to the candidate. Only the selected message's attachment metadata is transactionally changed;
stale selections are rejected, content-index entries invalidated and Activity recorded. Backup files
are never deleted, copy destinations never overwritten. Reopen already-open messages after repair.

RuleService.Analysis reuses matching with cached full body/CC/BCC, filters enabled/nonexpired rules
and account scope, follows SPAM-first order, and reports all condition matches including blocked later
rules after move/delete/SPAM. Flags competing destinations/read state, deletion with sends and identical
rule configurations. No actions run; missing cached matching data and simulation limitations are explicit.
Tests: AttachmentInvestigationTests plus existing rules, activity and accessibility/theme coverage.
Verification: 102 selected regression tests passed, including recovery through the view model and
candidate modification after hash verification. UI probes completed 4/4 in parchment/dark; final
captures in .codex-artifacts/attachment-investigation-ui-20261015-02. Production data was not used.

## 40. Indexed quick-search hang and wildcard choice (2026-10-15)

Captured the live v0.8.85 main thread stuck in SearchLocalMessagesAsync/ExecuteScalar
after a global Mitsubishi search. The original LEFT JOIN compared unindexed FTS identity
columns for each summary. Indexed quick predicates now select matching identity tuples
directly from FTS (including Notes), without that join. Wildcard scans use the existing
LocalMessageFtsKey lookup plus FTS rowid. No schema migration or index rebuild required.
Read-only SQL verification on the real database counted 411 Mitsubishi matches in 0.07s;
this measured the count query, not the entire UI operation. Production EQM was not stopped.

All quick searches now run on a worker; native sqlite3_interrupt is registered for cancellation.
The delayed search indicator exposes Cancel search, bound to the originating cancellation
source so an older indicator cannot cancel a subsequent search. Quoted terms disable wildcard
expansion. F3 asks Yes (wildcards), No/default (quoted literal word-index lookup), Cancel when
an unquoted wildcard is present. Quotes persist in history/saved views. The prompt explicitly
states that FTS ignores punctuation and filename searches use literal matching instead.

## 41. Review stabilization, without responsibility refactoring (2026-10-16)

ScheduledSendService retains accepted messages and their Scheduled copy until Sent archival
succeeds. TransportAccepted prevents SMTP replay, SentCopySaved checkpoints archival before
cleanup, and retry backoff also applies to accepted items. Cleanup failures retain the checkpoint.
Accepted items cannot be edited/replaced/undone; UI reports "Sent — archival pending", not an
SMTP error. Offline is rechecked after acquiring the dispatcher gate, between items and immediately
before transport. An already running SMTP send is allowed to finish.

Advanced search uses indexed FTS identity subqueries, including OR, rather than joining UNINDEXED
FTS columns. Date predicates are individually parenthesized. It runs on a worker with native SQLite
interrupt cancellation, stale-result guards, and a delayed cancellable indicator. No schema change.

Accounts/rules/saved views use ProtectedJsonFile: missing files are distinct from invalid/unreadable
files. Loading invalid data throws a visible diagnostic rather than returning an empty list; saving
validates the existing file before replacing it and retains its prior valid version as .previous.
Recovery is explicit, not silent (restoring obsolete accounts/rules could be unsafe).

quickmail.log and performance.log each rotate at 10 MiB with three numbered archives; individual
entries are capped at 64 Ki characters. Writes remain serialized and synchronous to preserve existing
crash diagnostic behavior. Manual Check for updates displays a dialog for no-update/disabled/error
results; background checks stay quiet. Existing update-available dialog is unchanged.

CI build/release now depends on protocol integration success. Test host nonzero exit and missing
discovered classes are fatal; discovery uses the same intentional exclusions as execution and fails
if empty. Do not restore the old blanket tolerance for WPF host crashes.

StabilizationRegressionTests exercises archival failure/restart, cleanup failure after archival,
offline in-flight/gate races, accepted-message edit prevention, configuration corruption/recovery,
advanced OR/date/scope/cancellation and indexed paging against 100,000 synthetic messages. These
tests use temporary profiles and mock transport, never live accounts or production data.

Verification: strict application rebuild (-warnaserror) passed with 0 warnings/errors; final
QuickMail suite 3,383 passed, backup core 75 passed, backup UI 15 passed. The test project still
reports its two pre-existing obsolete MimeKit constructor warnings in EmbeddedImageTests.
Workflow YAML and PowerShell blocks parsed locally; discovered-class coverage check found
278 classes with no missing classes. GitHub Actions itself was not triggered/published.
Results: .codex-artifacts/stabilization-20261016/stabilization-verified.trx, backup.trx, backup-ui.trx.

## 42. Attachment explorer display and query tuning (2026-10-16)

Size columns show MB (bytes / 1,048,576), N2 regional formatting and right alignment, while
SortMemberPath remains raw Size. Window Language follows CurrentCulture. Title binds to Items.Count
(displayed attachment references, not unique disk paths or uncapped total). Presentation tests verify
Spanish/English separators, numeric sort path, alignment and count updates.

BrowseAttachmentsAsync now filters s.has_attachments=1 before reading details and expanding JSON,
using the existing idx_summary_attachments_date index. SaveLocalMessageAsync and UpsertDetailAsync
maintain this bit; header-only UpsertSummariesAsync preserves it. Read-only audit of the real database
found all 296,757 non-inline attachment references flagged correctly (no unflagged references).
No schema/index changes or production data writes. Search still matches substrings in filename,
subject or sender, and reads only cached database metadata; directory recovery is separate.
Native SQLite cancellation and an "Attachment explorer: metadata search" timing entry were added.

Initial and manual attachment searches show SlowOperationWindow after two seconds, with scope,
live status, elapsed time and cancellation captured for that operation. Fast searches do not flash
a window. SQLite exposes no useful progress percentage here, so the bar is indeterminate.
The owned indicator closes on completion; a closed explorer cannot open a delayed indicator.

Read-only Python sqlite3 comparison on the 23.2 GB database: alternating three trials for "chistes"
gave medians 5.759s original vs 4.002s optimized (8 rows). Empty search: 5.407s vs 0.218s (10001 rows,
the extra row detects the UI cap); absent term: 5.349s vs 4.010s (0 rows). Full result fingerprints,
including order, matched in every comparison. These are SQL timings, not full UI timings or a cold-
cache guarantee. Alternative join orders/splitting OR did not justify extra complexity. Benchmark
script: .codex-artifacts/attachment-query-compare.py. Verification: 56 selected attachment/local-mail
tests passed, including refresh preservation, detail replacement and cancellation followed by reuse.

## 43. Attachment hover preview (2026-10-17)

AttachmentHoverPreview is shared by MainWindow's reading pane and MessageWindow. A non-activating
WPF Popup opens after 750 ms over a ListBoxItem, leaves the item accessible, and allows 400 ms to
cross from the attachment into the preview. Mouse-down/context menus/Escape, deactivation, hidden
attachment list, message changes and owner closure dismiss and cancel it. Rendering completions
check cancellation so closed previews cannot be repopulated. Existing open/save/drag handlers are
unchanged; hover never marks mouse clicks handled. The size tooltip is suppressed while previewing.

Allowlist: JPG/JPEG/PNG/GIF/BMP, PDF, TXT. Images decode off-thread to frozen bounded bitmaps (GIF
first frame). PDF uses Windows.Data.Pdf rasterization with page buttons; no WebView or active links.
TXT uses BOM-aware StreamReader, preserves newlines and truncates at 64K characters. Actual input
is limited to 25 MiB, images to 100M source pixels; rendered dimensions are bounded. Missing local
paths remain visible, never silently re-downloaded. Remote attachments require explicit download
and are cached in the existing AttachmentModel.Content; no preview temp files are written.
ConfigModel/SettingsViewModel/SettingsDialog expose AttachmentHoverPreview, enabled by default.
No new dependencies, database schema changes, production data writes or packaged EXE in this turn.
Validation: 44 selected preview/settings/attachment path and drag tests passed, including native
two-page PDF rendering, image downsampling, text BOM/newlines/truncation, cancellation during load,
unhandled mouse clicks, file release, missing paths and explicit remote-download policy.

2026-10-18 layout correction: the stretched ListBoxItem was causing WPF's Right placement to
flip the popup to the left of the entire row. Both attachment templates now name the filename
label AttachmentPreviewAnchor; the preview locates this visual and positions beside its natural
width. Custom placement prefers the right, with a 900 x 1000 DIP maximum and vertical clamping to
the anchor monitor's working area (device coordinates converted to DIPs). Smaller screens adapt;
left placement is a fallback only when the right has insufficient space. PDF/text/image viewports
use the calculated size instead of the original 380px content height and 440px PDF width.

## 44. Multiple client-rule actions and flags (2026-10-18)

MailRule retains legacy Action/TargetFolder/ForwardTo/ReplyTemplateId fields and adds FlagId and
AdditionalActions (RuleActionStep). No database schema change. GetActionSteps exposes execution
order with the sole destination last; SetActionSteps keeps that destination in the legacy fields
so canonical folder rewrites, destination tabs and source-removal logic continue working. Legacy
single-action JSON remains valid. Do not downgrade to an old EXE after creating multi-action rules:
old binaries do not understand AdditionalActions. Semantic/dedup keys include the complete actions.

RulesManager exposes an action list, add/remove and a selected-action parameter editor with
conditional Move/Forward/Reply/Flag panels. AlsoMarkAsRead remains independent/default true.
Quick-move creation never overwrites an existing multi-action rule, and Cleanup excludes them.
The unified Microsoft manager routes client editing to this editor; the server editor rejects
unsupported client actions rather than silently discarding them. Server rules are unchanged.

Execution validates destination count/action parameters first, checks flag definitions, then uses
existing per-action code (same rule Id retained for forward/reply deduplication). Queued forward
completion precedes moving; SMTP delivery is not awaited. Mark-read is applied before a terminal
action or after nonterminal actions. Multi-action read/unread/delete failures propagate and stop
later steps, reporting completed actions and possible partial batch progress. No transaction/rollback
across remote actions is claimed. Flag uses IFlagService for server mirroring and explicitly checks
local persistence (the interactive flag service otherwise tolerates unavailable local storage).
At most one destination and one reply template; multi-action MarkUnread + AlsoMarkRead is rejected.
SPAM stays specialized. Rule previews, diagnostics and the unified summary include all actions;
per-step FilterOperationTrace reports progress. No production rules/database modified in this turn.
Verification: main project Release build with -warnaserror passed (0 warnings/errors); 235 selected
rule/flag tests passed, including JSON compatibility, folder rewrites, queue-before-move ordering,
forward/read/move failures, cancellation, missing flags, persistent flag set/clear, quick-rule
non-overwrite, previews and WPF conditional parameter panels. Test compilation retains the two
pre-existing obsolete MimeKit constructor warnings in EmbeddedImageTests. No autonomous EXE built.

### 45. Attachment preview layout and menu (2026-10-18)

Preview now uses a Grid with a flexible viewport and an auto-sized bottom navigation row;
single-page PDFs have no navigation controls. Raster images fit the available width and
reduce the popup height to their rendered height plus header/padding, capped at the monitor
limit (tall images scroll). Custom placement uses the resulting popup height.
Right-click on the preview opens the original attachment menu, selecting the hovered attachment
and retaining the ListBox as PlacementTarget for existing commands and click handlers.
The preview stays open while this menu is open, unless the message changes or the owner deactivates.
Focused tests cover bottom placement, one-page PDF, short/tall images and menu targeting;
358 preview/rule/flag tests passed (including 34 focused preview/multi-action tests).
Local OAuth-enabled standalone version 0.8.86.4,
including the multi-action filters above; no GitHub release or production database changes.

### 46. Inline rule actions and Execute (2026-10-18)

RulesManagerWindow now shows the read checkbox first, then separated inline action editors.
Add action sits beside the Actions heading. Each dropdown ends with a UI-only -1 Remove action
option, intercepted before persistence; removing all blocks leaves a read-only rule, or validation
fails if the read checkbox is off. Move/Delete blocks automatically sort last (still at most one
destination). Folder/template/flag/recipient controls bind to their own step; Escape respects any
open combo. Forward warns live for multiple addresses and recommends separate Forward steps.

RuleAction.Execute is appended to the enum, retaining old persisted numeric values. CommandLine
and AddSubjectToCommandLine are copied through legacy primary fields, extra steps, cloning,
semantic deduplication, previews and server-editor guards. RuleExecuteService parses Windows
arguments and uses ProcessStartInfo.ArgumentList, UseShellExecute=false, a hidden process and
a 60-second timeout. Runs once per matched message, waits before later actions; nonzero exit or
timeout fails the action, cancellation kills the process tree. No rollback or exactly-once guarantee.
Requires an .exe; scripts use an explicit interpreter. Subject is appended as one data argument;
cmd.exe is rejected with subject enabled, and PowerShell requires -File rather than -Command.
Trust/security boundary: user-configured code runs as the current Windows user, not sandboxed;
scripts must not evaluate subject text, and sender matching is not authentication. No actual mail
commands or production rules/data were executed/modified during development.

Verification: Release main build with analyzers disabled and warnings-as-errors passed, zero
warnings/errors. 359 selected rule/flag/type-ahead tests passed, plus the added inline-dropdown
binding/removal test passed separately (360 distinct tests). Includes real benign process exit
code checks, argument safety, JSON compatibility, Execute-before-move/failure, parameter panels,
sorting/removal and recipient warnings. Test compilation has two existing obsolete MimeKit
constructor warnings. User guide updated. No new standalone EXE or GitHub release in this turn.

### 47. Connection-test progress and diagnostics (2026-10-18)

AccountEditorViewModel exposes operation-specific IsTestingConnection/ConnectionTestStage and
cancellation. AddAccountDialog and AccountManagerDialog attach a shared ConnectionTestIndicator
using SlowOperationWindow, immediate indeterminate progress, elapsed seconds and Cancel test.
Result closes the indicator before showing the report. No email is sent. POP3 reports connect,
authenticate, UIDL and disconnect; SMTP/IMAP report connect (MailKit combines DNS/TCP/greeting/TLS),
OAuth retrieval and authentication, SMTP disconnect. AsyncLocal ConnectionTestTrace isolates
progress from normal background activity and records per-phase elapsed times in performance.log.
Failure reports include endpoint/transport mode, current phase, elapsed duration, exception chain,
socket/native error, SMTP response code where available and actionable non-speculative advice.
Known password is redacted; no protocol transcript/tokens are collected. Reports also enter
quickmail.log; final dialog dwell time is excluded from connection-phase timing.

Read-only diagnosis of reported ronald@dinamica.tech failure: existing log verifies
email.dinamica.tech:587, StartTlsWhenAvailable. During this turn DNS resolved to 37.187.72.55;
TCP connected in ~0.1 seconds, SMTP greeting/EHLO succeeded, STARTTLS negotiated TLS 1.2 with
default certificate validation. No authentication, message sending or account setting changes.
This does NOT establish credentials are valid or explain the earlier intermittent timeout.
100 selected connection/provider/SMTP/POP3 tests passed on the final code;
added tests cover cancellation, indicator state, nested scope isolation, endpoint/native errors
and password redaction. No new standalone EXE published for this change yet.

### 48. Scheduled Send immediately and taller dialogs (2026-10-18)

MessageContextMenu exposes Send immediately only for a nonempty all-Scheduled selection and
the real ScheduledSendService. It queues selected messages for immediate background dispatch;
reports per-message failures without claiming SMTP success. SendNowAsync takes the queue gate,
validates account/local identity, blocks in-flight and already accepted messages, and respects
offline/maintenance. Durable SendNowRequested bypasses the original due time while retaining
retry backoff and the original SendAtUtc recurrence anchor. Replacing/editing a schedule clears
the flag; repeated successors use their normal calendar schedule with no force-send flag.
No mails were sent or production queue modified during development.

Rules Manager default height: 1000 DIPs (was 874); Manage Accounts: 960 (was 660).
Both cap initial height at 95% of SystemParameters.WorkArea.Height and retain scrolling for
small screens/expanded settings. Release build passed with warnings-as-errors/analyzers disabled;
18 selected scheduled-send/repetition/account/editor tests passed, including selective early send,
unchanged other schedules, preserved recurrence and offline refusal. A further 91 local-first,
repeated-send and stabilization regression tests passed. No new standalone build yet.

### 49. Home dashboard and per-application inactivity (2026-10-18)

App initializes a permanent Home tab explicitly (headless MainViewModel tests retain their old
tab set). MainWindow.Home.cs builds a scrollable dashboard: independent receive/send evidence,
error-first account rows, local backup receipts only, queue summary, summary-column statistics
for today/yesterday/week/month with account selector, and all-account quick-search links.
AccountHealthJournal persists last attempts/success/error types in account-health.json, without
exception bodies or secrets. Actual outgoing sends, POP3 sweeps, full Inbox sync and connection
failures feed it; saved-account IMAP/POP3/SMTP tests also update the corresponding channel.
Unknown or old observations are explicitly not described as proof of current health. Evidence
starts with this build. No periodic SMTP probe is introduced.

Backup uses Catalog.ReadRecentReceipts(includeDestination:false), never opens a NAS connection.
External windows do not count as successful backups. Statistics run in Task.Run, touch summary
columns only, group Message-ID within each account, omit drafts/scheduled from counts, exclude
Trash/Junk/RecoveryDeleted from unread pending. SQLite interrupt gives statistics a 20-second
budget; cache lasts five minutes. Counts reflect locally cached mail and known message direction,
not the complete remote mailbox. Quick-search buttons explicitly say all accounts/all folders.

ShowHomeAfterInactivity defaults on and is exposed in General settings. Input in this WPF app
resets a monotonic 15-minute clock; one switch per idle episode. Editors (including docked tabs),
visible other windows, busy operations, maintenance/update and pressed mouse buttons defer it.
It does not activate/foreground the window. Home uses existing list-state snapshots; returning
restores rows, search and selection. Hidden-message keyboard actions/selection are guarded.
Health/statistics failures are displayed, not allowed to block opening mail. No standalone EXE
or release published for this change; latest local standalone remains 0.8.86.6.

Validation: 157 selected Home/search-tab/connection/tab/sync/local-first tests passed, including
100,000 synthetic summary rows, account scoping, Gmail label deduplication, date boundaries,
unread exclusions, cancellation, durable independent health channels, configuration roundtrip,
idle deferral and preserved rows/selection. Production DB and mail queues were not modified.
The test project still emits its two pre-existing CS0618 MimeKit constructor warnings.

### Home dashboard mockup redesign (2026-10-18)

Implemented the supplied Home-html.zip reference as native WPF in HomeDashboardView, not by
running its bundled React/JavaScript. Responsive cards, actionable attention rows, per-account
receiving/sending table, cached account-specific activity with daily averages, backup/restore
cards, colored Today events and folder-review links. Production values come from local summaries,
health observations, scheduled queue and local backup receipts; screenshot fixtures are synthetic.
Home does not contact NAS or servers on display. Explicit account checks support progress/cancel;
SMTP tests authenticate without sending; Graph cannot prove sending permissions with an SMTP test.
Targeted incoming checks reuse existing sync/POP pipelines and do not explicitly dispatch Scheduled.
Backup agent accepts /tab for Native Backup, Restore test and History navigation.
Unread Home searches exclude Trash/Junk/RecoveryDeleted and retain this scope in tab snapshots.
Statistics read all accounts together, then selector changes use memory, not another DB query.

Validation: 155 selected Home/search-tab/connection/main-tab/local-first/sync tests passed, including
100,000 synthetic summaries. Native view rendered at wide and narrow sizes without opening a window.
Preview images: Artifacts/home-redesign-preview (test data only). Backup agent build: zero warnings
and errors. No production DB changes, live mail tests, executable publication or GitHub release
performed for this redesign.

Home refresh hotfix: 0.8.86.8 displayed Home once then cleared it on its second Render.
Production log confirmed InvalidOperationException (logical child already has a parent).
The persistent status TextBlock remained attached to the detached header's WrapPanel.
Render now removes it from its old parent before constructing the replacement header.
Added a regression covering loading-to-statistics refresh, account selection and later refresh;
it failed before the fix. No database or account changes involved.

### Home Google Calendar integration (2026-10-24)

Home now shows Google calendar account status (including explicit CalendarProvider=google),
enabled/disabled, last successful sync, unknown/stale/error and individual Sync now / Configure.
GraphCalendarSyncService records an independent Calendar health channel after successful full,
single-account or day sync and on errors; cancellations leave prior evidence unchanged. No raw
provider exception payload is persisted in health JSON. Opening Home only reloads local calendar
rows via ReloadFromStoreAsync, not provider APIs. Explicit retries use the existing single-account
sync with progress, cancellation and a two-minute bound, respecting Offline/Focus/maintenance.
Today uses the existing global recurrence-aware TodayEvents across sources, all-day/time range,
account/calendar labels and colors. Double-click routes through ActivateSelectedCalendarEvent;
existing invite and read-only behavior remains. Cached events are retained with a stale/unverified
warning, and empty unverified agendas are not presented as confirmed empty days.
Validation: 125 selected Home/Google/Graph/per-account/calendar-VM tests passed with stub HTTP and
temporary databases; no live account sync or executable publication performed for this change.

Local standalone 0.8.86.10 was subsequently published with OAuth, Assets, BackupAgent and Updater
to Artifacts/local-0.8.86.10-win-x64-bfbe5abb02dc48c2b1571f9edbe4e39f (not GitHub).

Ctrl+Enter search fix (2026-10-26): search input now consumes Enter before starting/awaiting
the asynchronous query, captures the Control modifier once and ignores autorepeat. The window
handler leaves search-editor Enter to that handler rather than dispatching a global mail command.
Escape is also consumed before awaiting clear. Added input-timing/modifier and cross-folder,
cross-account scope tests. This fix is newer than the 0.8.86.10 standalone.
