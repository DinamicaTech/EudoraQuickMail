# Eudora QuickMail — AI handoff

> Living specification and technical handoff for continuing the project without the
> original conversation history.

- Last reviewed: 2026-09-07
- Code snapshot audited: `codex/local-first-fork` at `3b2a1f6`
- Fork base: upstream QuickMail commit `deb82e88aa80cdca9e2f087a345c727c9b3ab9f9`
- Current product version: `0.8.62`

## 1. Purpose

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
- Draft, Scheduled, Sent/Out, Trash and Junk system folders; scheduled delivery.
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
- Restricted local Subject/body edit, Send Again, Reply/Reply All/Forward.

### 4.5 Rules and rapid classification

- Global/account rules, automatic incoming versus manual modes.
- From, To, optional Cc/Bcc, Subject, Body and attachment conditions.
- Move/read/unread/delete and move-plus-mark-read actions.
- Compatible-rule view, rule-definition search, cleanup and apply-to-folder.
- Keyboard/drag quick-filter workflows and optional manual Out-mailbox classification.
- Optional background destination tab showing where the last filtered message went.

### 4.6 Compose and language tools

- HTML default; docked-tab or floating compose, with docked preferred.
- HugeRTE formatting, links, lists, colours, fonts, charmap, emoji, preview and word count.
- Editable HTML reply/forward quote, account HTML signatures and recipient autocomplete.
- Draft autosave/recovery, templates and scheduled delivery.
- Spanish/Catalan/English spelling and one-shot language detection.
- Selected-text Argos/DeepL translation and local LanguageTool grammar checking.

### 4.7 Calendar, shell and maintenance

- Google Calendar OAuth, FullCalendar views, day sync and direct event actions.
- Incoming ICS recognition for POP3/IMAP/Graph, with RSVP cards for meeting requests and explicit
  Add to Calendar for standalone publications; the original `.ics` remains an attachment.
- Compact Today agenda with calendar selector.
- Separate taskbar notification/tray-icon settings and custom received icon.
- Early splash, single-instance recovery, shutdown hardening and structured performance log.
- Search/index/count maintenance, orphan attachment manager and safe embedded de-duplication.
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

System folders sort first: **In, Draft, Scheduled, Out/Sent, Trash, Junk**. Root-level
`Inbox`/`INBOX` bind into visible `In`. `_In`, `_Out`, `_Trash` are ordinary historical
folders, not hidden aliases.

## 7. Accounts and synchronization

### 7.1 POP3/SMTP

- Check Mail processes only active accounts with `Check incoming email` enabled.
- Status identifies account and message progress.
- Receive means MIME parsed, rows committed and local visibility established before server
  deletion is attempted.
- Sent mail is local outgoing mail with delivery status.
- A scheduled SMTP failure remains in Scheduled with its error/attempt state and is retried by a
  later dispatcher pass; it never becomes Draft merely because the Internet connection was down.
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

Commands: Test, Save, Cancel, Run on Existing Mail, Save and Apply to current folder, and
Cleanup. Save closes; Cancel discards. Cleanup removes move rules with missing/undefined
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
user deliberately deleted. Send Later asks date/time and creates/reuses Scheduled. Recipient
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

### 15.3 Startup/shutdown

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
