# Eudora QuickMail compared with the original QuickMail project

## Executive summary

**Eudora QuickMail** is a fork of [QuickMail](https://github.com/kellylford/QuickMail)
that builds on its strong foundation as an accessible Windows email client and develops it into a
**local-first** application designed to preserve large historical mail archives and provide a
dedicated migration path from Eudora.

QuickMail already provided the essential foundations: a WPF application for Windows, complete
keyboard operation, multiple IMAP/SMTP accounts, a unified inbox, conversation views, WebView2
HTML rendering, and secure credential storage. Eudora QuickMail preserves that foundation and
extends it in six main directions:

1. durable local storage and offline operation;
2. POP3 reception and SMTP delivery integrated with the local data model;
3. migration of Eudora data and safe cloning of profiles from the original QuickMail;
4. search and indexing designed for large mail archives;
5. advanced classification, composition, calendar, and productivity workflows;
6. standalone Windows distribution, diagnostics, and profile maintenance.

The difference is not merely one of branding or visual identity. The fork changes the storage
model, synchronization architecture, folder organization, and many user workflows so that the
local archive can be the primary source of truth instead of serving only as an IMAP cache.

## Comparison baseline

This comparison uses QuickMail commit
[`deb82e8`](https://github.com/kellylford/QuickMail/commit/deb82e88aa80cdca9e2f087a345c727c9b3ab9f9),
identified by the project as the fork baseline. It describes Eudora QuickMail through version
**0.8.73**.

QuickMail may continue to evolve independently. In this document, “the original project” means
**the specific baseline from which this fork was created**, not necessarily the latest version
currently available in the upstream repository.

## At-a-glance comparison

| Area | Original QuickMail at the fork baseline | Eudora QuickMail |
| --- | --- | --- |
| Product focus | Modern, accessible, keyboard-first Windows email client | Local-first client and practical Eudora successor for large historical archives |
| Protocols | Multiple IMAP/SMTP accounts and supported services | Retains IMAP/SMTP and adds local accounts with safe destructive POP3 retrieval and SMTP delivery |
| Data authority | The IMAP server is the primary authority and SQLite acts as a cache | The local SQLite store can be the primary authority; mail remains available offline |
| Attachment persistence | Attachment handling follows the server/cache model, without a dedicated independent on-disk attachment corpus | Locally retained received, embedded, and migrated attachments are materialized as independent files below the profile's `Attachments` tree; SQLite keeps their references and metadata |
| Eudora migration | No dedicated migration tool | Guided migration of Eudora mailboxes, hierarchies, accounts, filters, headers, HTML, and attachments |
| Upgrade from QuickMail | Uses its existing profile directly | Creates a validated, independent copy of a QuickMail profile and opens it offline for review |
| Organization | Unified inbox, account and folder trees, and conversations | Canonical folder tree shared by accounts, aggregate roots, and local system folders |
| Search | Mail search and folder navigation | Advanced indexed search, compact query language, and attachment-content indexing |
| Rules | Mail rules and filters | Global or account rules, automatic/manual modes, preview, and rapid classification workflows |
| Composition | Plain text, Markdown, and HTML; replies, forwarding, and drafts | Rich HTML editor, per-account signatures, scheduling, durable send queue, Undo Send, and language tools |
| Calendar | Existing calendar integrations | Independent Google Calendar identities, agenda views, direct actions, and ICS handling |
| Offline work | Greater reliance on the server for remote mail | Offline and Focus modes, a usable local archive, and persistent local operations |
| Large archives | General-purpose email client | Designed and optimized for hundreds of thousands of messages, with a working target near 600,000 |
| Distribution | Project build and generated executable | Standalone executable and Windows installer with separate program and data locations |
| Diagnostics | Logging and basic connection controls | Account Health, performance timing, actionable errors, and repair and maintenance tools |

## Capabilities inherited and preserved

Eudora QuickMail does not start from scratch. It retains and explicitly recognizes essential
capabilities from the original project:

- a 64-bit WPF desktop application for Windows 10 and Windows 11;
- an accessible interface that can be operated entirely from the keyboard;
- multiple accounts and combined mailbox views;
- IMAP/SMTP compatibility;
- conversation grouping;
- a WebView2-based HTML reading pane with security restrictions;
- concurrent IMAP connections so synchronization does not block message reading;
- credentials protected through Windows security facilities;
- an address book, message composition, attachments, themes, and configurable shortcuts;
- the MIT license and attribution to the original project and its author.

The fork aims to remain compatible with existing QuickMail internal identifiers and profiles while
adopting the public name **Eudora QuickMail**.

## Major Eudora QuickMail improvements

### 1. Local-first architecture

The most significant extension is a model in which messages can remain durably stored in SQLite
and continue to be available without a server connection.

- POP3 accounts download each message and request its removal from the server only after local
  storage has been confirmed.
- Messages retain their account identity even when multiple accounts share the same visible folder
  root.
- Drafts, scheduled messages, sent mail, snoozed mail, trash, and junk have persistent local
  representations.
- IMAP remains available for community compatibility and is also integrated with local search and
  navigation.
- **Work Offline** and **Focus** modes can suspend all network activity or incoming checks only.
- Bulk move, flag, read-state, trash, and permanent-delete operations are adapted for large local
  result sets.

### 2. Dedicated Eudora migration

Eudora QuickMail includes a purpose-built migration tool available during first run and from within
the application.

- It can import a complete Eudora installation or a selection of `.mbx` and `.fol` files.
- It reconstructs mailbox hierarchies and preserves dates, plain-text and HTML bodies, RFC/MIME
  headers, and attachment references.
- It recovers real-world historical data containing malformed mbox records, `Content-Length`
  fields, `<x-html>` content, and embedded images.
- It imports POP3/SMTP personalities, recognizes the dominant personality, and can recover Eudora
  passwords before immediately protecting them again with Windows DPAPI.
- It converts supported `filters.pce` rules into Eudora QuickMail rules and reports rules that
  cannot be converted.
- Referenced attachments can be kept in place, copied, or moved.
- Progress, diagnostics, finalization, and indexing phases are presented explicitly.

Existing QuickMail users have a separate migration path. Eudora QuickMail snapshots the original
SQLite database, copies compatible profile settings and user data into a new profile, validates the
result, and starts the copy offline. The original QuickMail profile is not modified.

### 3. Local folders and shared account roots

The folder tree becomes a canonical view of the archive rather than a simple reflection of one
mail server.

- Multiple accounts can share a logical root and present combined Inbox, Draft, Scheduled, Sent,
  Trash, and Junk folders.
- Roots and folders can act as recursive views with paging, sorting, and search.
- Folders can be created, renamed, moved, dragged, and merged, with rule destinations updated when
  paths change.
- Total and unread message counters include descendant folders and update immediately.
- Column layout, sorting, preview state, and pane sizes are preserved for each screen resolution.

### 4. Expanded search and indexing

Search is designed for local archives containing hundreds of thousands of messages.

- Indexed searches cover sender, recipients, Cc, subject, body, and dates.
- A compact query language supports field prefixes, comparisons, `AND`/`OR`, quoted phrases, and
  the single-character `?` wildcard.
- **Search Everywhere** runs the query across all relevant active accounts without depending on the
  currently selected folder.
- Searches can be explicitly saved as smart views or virtual folders.
- A deduplicated history retains the ten most recent queries.
- Attachment names and content can be indexed for text, PDF, Office, and archive formats, with
  safety limits and SHA-256 result reuse. OCR is not performed.
- Repair and rebuild commands, timing metrics, and folder sender-domain analysis are included.

### 5. Rules and rapid classification

The rule system is extended to handle automatic incoming mail as well as manual classification of
an existing archive.

- Global and per-account rules.
- Conditions for From, To, optional Cc/Bcc, Subject, Body, and attachments.
- Move, mark read or unread, delete, and move-and-mark-read actions.
- Keyboard and drag workflows for creating and applying filters from messages.
- Existing-folder application, rule-definition search, and cleanup tools.
- A non-destructive preview showing counts and sample matches before a rule is applied.
- An optional background destination tab showing where the last classified message was moved.

### 6. Composition, sending, and language tools

Composition is designed for intensive, multilingual use.

- HTML is the default mode, using HugeRTE for formatting, lists, links, colors, fonts, special
  characters, emoji, preview, and word count.
- Composition can take place in a docked tab or a separate window.
- Per-account HTML signatures and editable original content in replies and forwards.
- Draft autosave and recovery, templates, and scheduled delivery.
- A durable outgoing queue stores the complete message before the editor closes and retries
  transient failures without blocking the interface.
- A configurable sending delay and **Undo Send** action can recover a queued message as a draft
  before transport begins.
- A contextual warning detects when newly written text mentions an attachment in English, Spanish,
  or Catalan but no file has been attached.
- English, Spanish, and Catalan spell checking, language detection, and a personal dictionary.
- Selected-text translation through local Argos Translate or optional DeepL.
- On-demand grammar checking with LanguageTool running on the user's own computer.

### 7. Calendar and invitations

- A mail account can link a Google Calendar identity independently of its mail authentication.
- Day, week, and month views, a compact Today agenda, and focused synchronization are provided.
- Events can be duplicated or shared by email as ICS attachments.
- Invitations and `text/calendar` or `.ics` publications are recognized consistently across POP3,
  IMAP, and Microsoft Graph.
- Meeting requests expose RSVP actions, while standalone publications can be added to the calendar
  without hiding the original ICS attachment.

### 8. Reading, export, and attachment handling

- Locally retained received, embedded, and migrated attachments are stored as ordinary independent
  files below the profile's `Attachments` directory rather than being usable only as binary content
  inside the SQLite database. The database records their paths and metadata.
- This separation makes the attachment corpus easier to inspect, back up, recover, and maintain
  with normal file tools, while also preventing large attachment payloads from unnecessarily
  inflating the primary mail database.
- Received POP3 attachments are organized into predictable category and year folders. Existing
  outgoing source files can remain referenced, and Eudora imports respect the user's selected
  keep, copy, or move strategy.
- The preview can be detached to a second monitor and configured to follow the main selection.
- One or more messages can be exported as EML or HTML, their original headers can be copied, and
  their source can be inspected.
- EML reconstruction includes attachments that are locally available or can be downloaded.
- Attachments can be opened, saved, and dragged to File Explorer; files can also be dropped onto
  the HTML composer.
- Stored messages support restricted local editing of subject and body.
- Links display their real destination domain, and unsubscribe links open in a restricted,
  ephemeral WebView2 profile after confirmation.

### 9. Reliability, diagnostics, and maintenance

- An early startup screen reports progress, and single-instance handling can recover from an
  unresponsive previous process.
- Connection and transport failures identify the account, operation, server, and likely corrective
  actions.
- **Account Health** summarizes configuration, credential availability, last synchronization, and
  scheduled-queue status.
- Structured performance logs capture startup, folder, search, and slow-operation timings.
- Tools rebuild indexes, recalculate counters, and locate orphaned attachments.
- Embedded resources controlled by QuickMail can be safely deduplicated.
- Shutdown handling has been strengthened to avoid races and incomplete background work.

### 10. Installation and distribution

- A self-contained Windows x64 build avoids requiring users to install the .NET runtime separately.
- The Inno Setup installer allows separate program and data locations.
- Application binaries and the mail profile can be maintained in different directories.
- The application can register as a candidate for the default Windows mail client.
- Both the executable and installer can be distributed through GitHub Releases.

## Who benefits most from the fork?

Eudora QuickMail is especially suited to users who:

- retain years or decades of email in Eudora;
- need to work with and search a local archive without a network connection;
- use POP3/SMTP or combine those accounts with IMAP;
- want multiple identities to share a single archive tree;
- manage hundreds of thousands of messages and attachments;
- prefer fast keyboard workflows and an accessible interface;
- want local control over messages, indexes, dictionaries, and language tools.

For users who primarily need a straightforward IMAP client with a unified inbox, the original
QuickMail project remains a smaller and more direct foundation. Eudora QuickMail's main advantage
appears when historical migration, durable local storage, classification, and advanced search are
central requirements.

## Current scope and limitations

- Eudora QuickMail is under active development, and current releases should be considered
  preliminary.
- PDF and Office document indexing extracts text but does not perform OCR on images.
- Outlook PST import is outside the current scope.
- Some integrations require user-supplied credentials, including Google Calendar, Microsoft OAuth,
  and DeepL.
- The published installer may not yet be code-signed; Windows can display a reputation warning
  until signing is available.
- Large archive migrations should be validated and backed up before the original Eudora copy is
  removed.

## Origin, attribution, and license

Eudora QuickMail is derived from the open-source QuickMail project created by **Kelly Ford** and
retains its copyright and MIT license. The fork's name acknowledges both parts of its history:
**Eudora**, for the workflow and historical archives it aims to preserve, and **QuickMail**, for
the project that provides its technical and accessibility foundation.

For more detailed information, see:

- [Fork development log](../FORK_CHANGELOG.md)
- [User guide](../USERGUIDE.md)
- [General changelog](../CHANGELOG.md)
- [Original QuickMail repository](https://github.com/kellylford/QuickMail)
- [Eudora QuickMail repository](https://github.com/DinamicaTech/EudoraQuickMail)
