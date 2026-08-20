# QuickMail fork development log

This file records the functionality developed on top of the original
[QuickMail](https://github.com/kellylford/QuickMail) project. It complements the upstream
`CHANGELOG.md`; upstream authorship and the original MIT license are preserved.

The first entry is a consolidation of the prototype work completed before the fork began using
incremental commits. Subsequent work should be added as normal Git commits and summarized here
before a public release.

## Unreleased

- Automatically starts the attachment-content index after first import when messages have
  attachments but the index is empty, with an explicit notice and highlighted background progress.
- Added an immediate startup splash with live phase text until the startup folder is ready, and
  routed non-wildcard `AC:` attachment-content searches through SQLite FTS instead of scanning all
  extracted attachment text with `LIKE`.
- Added a one-time post-Eudora-import reminder when none of the imported accounts has a stored
  password, and turned the empty Today agenda into a Google Calendar setup link when no Google
  calendar identity is connected.
- Made the second Eudora migration phase explicit and continuously visible: the importer now shows
  profile-import stages, warns users not to close the window, and prints an elapsed-time heartbeat
  every ten seconds while copying messages and building the full-text index.
- Adopted the definitive public product name **Eudora QuickMail** across window titles, onboarding,
  installer, shortcuts, package metadata and documentation. Internal executable, profile and
  credential identifiers remain compatible with existing QuickMail data.
- Rehabilitated the original Standard IMAP/SMTP account path as an optional backend alongside
  local POP3/SMTP accounts. Manual Check Mail now reconnects eligible remote accounts, synchronizes
  each IMAP/Graph Inbox, reports per-account progress, then continues with POP3 and queued sending
  even when one remote account is unavailable.
- Google OAuth Client ID/Secret can now be maintained in Settings and are validated before browser authorization, replacing invalid `client_id`-less requests with an actionable local error.
- Any non-shared mail account can now link an independent Google Calendar identity without changing its POP3/SMTP authentication; calendar refresh tokens are isolated in Windows Credential Manager and the account manager supports connect, reconnect, and disconnect.
- Folder-tree roots are now selectable recursive mail views, with paging, sorting, and search constrained to the accounts sharing that root; Settings can hide Combined views and Calendar independently.
- Read/unread changes now update recursive folder badges immediately, and badge counts use the current regional thousands separator.
- Scheduled rows now show `Scheduled` or `SMTP error` in the Status column; folder badges count unread mail recursively and total items for Trash/Scheduled.
- Reopened scheduled messages now restore their saved HTML/Markdown/plain-text mode and do not append the signature a second time.
- Shared roots now present Inbox, Draft, Scheduled, Sent, Trash, and Junk as one logical aggregate folder across their member accounts.
- Local drafts and scheduled messages now atomically create their system-folder catalogue row and refresh the tree, preventing invisible queued mail.
- Accounts can now share a named logical folder-tree root while messages retain their original account identity.
- The Eudora import confirmation now explicitly explains that it replaces only the previous Eudora message import and preserves all other accounts and mail.
- Connection test results now open in a readable modal dialog instead of being hidden behind the account form buttons.

- Added local `Draft` and `Scheduled` system folders to imported Eudora/archive accounts.
- Moved the New/Reply/Reply All/Forward toolbar above the message list.
- Restored the full-result vertical scrollbar and added a reading-pane `Detach / Follow Selection` action.
- Added visible indeterminate progress while Argos runtime and language packages are installed.
- Fixed the total-result scrollbar layout, kept the inline preview closed while a detached follower
  is active, and ensured local archive accounts run their system-folder migration without credentials.
- Preserved persisted special-folder enum values, repaired local system-folder kinds by name, and
  simplified detached preview windows so they always follow the main selection.
- Reopened local HTML drafts in their original mode, closed compose after Save Draft, added editable
  scheduled messages (reschedule or Send immediately), and added a regional `Time` message column.
- Added full-form account editing from Manage Accounts and a `Check Mail` toolbar action that
  manually checks every POP3 account, dispatches due queued mail, and reports progress in Status.
- Protected local archive deletion with an explicit destructive warning and moved its potentially
  long SQLite cascade off the UI thread so Manage Accounts remains responsive.
- Added an `Active account` flag; inactive accounts remain configured and keep their data but are
  excluded from the main account tree, connection, POP3 checks, and scheduled dispatch.
- Fixed Test Connection for POP3/SMTP accounts so it validates POP3 + SMTP (including UIDL) without
  requiring or probing IMAP; manual Check Mail now truly includes automatic-check-disabled accounts.

- Added visible local Draft and Scheduled folders; Send Later now prompts explicitly for a local
  date/time and keeps a local scheduled copy until successful delivery.
- Folder unread badges now include descendants and update recursively.
- Reduced the main toolbar to New, Reply, Reply All, and Forward; moved Empty Trash to Tools.
- Added DeepL usage/balance lookup, repaired Argos user-install executable discovery, and condensed
  setup output so successful `pip` diagnostics no longer overwhelm the provider window.
- Eudora migration now imports POP3/SMTP account personalities from Eudora.ini without passwords.
- Added a Follow Selection action to standalone message windows for a reusable second-monitor view.
- Attachment index verification no longer runs automatically at every startup; Tools now exposes an
  explicit Update Attachment Index action alongside full index rebuilding.

- Added a dedicated compose status bar and wait cursor for grammar checks, template-name prompting
  with the first meaningful body line as default, and reliable dismissal of the HTML context menu.
- Restored fast unprefixed full-text search, added the standalone `N` unread criterion, message
  read/unread context actions, and a folder column in the message grid.
- Documented unprefixed and unread quick searches, fixed read/unread actions appearing in the wrong
  context menu, and removed the unread accent bar that shifted grid rows out of column alignment.

### Local-first mail

- Added local-only accounts with SMTP sending and destructive POP3 retrieval: a message is removed
  from the server only after it has been stored successfully.
- Added per-account incoming-mail polling control and Windows DPAPI protection for account secrets.
- Added local folders and container-only parent folders, scheduled sending, local Sent/Drafts/Trash
  handling, bulk move/delete operations, and fast local full-text search.
- Added configurable, virtualized message pages, recursive virtual-folder views, account-neutral
  message browsing, selection counts, progress status, sortable/resizable columns, and persistent
  pane sizes.

### Eudora migration

- Added an Eudora mailbox importer and an in-application migration command with progress and
  automatic QuickMail restart.
- Preserved mailbox hierarchy, message dates, text and HTML bodies, raw RFC/MIME headers, and links
  to existing Eudora attachment files without copying attachment bytes into SQLite.
- Added recovery for malformed mbox records, Eudora `Content-Length` handling, inline-image display,
  import diagnostics, FTS indexing, and repeatable search benchmarks.

### Search and message operations

- Added simple and advanced local search across From, To, Cc, Subject, Body, and date criteria.
- Added AND/OR advanced criteria, session-persistent search forms, search clearing, folder-scoped
  searches, Alt+cell filtering, filter-rule application, permanent multi-message deletion, and
  status feedback for long operations.
- Added message source viewing, expanded `Bla bla bla` header/content inspection, attachment opening
  with executable-file warnings, and drag-and-drop folder moves.
- Added a compact quick-search language with field prefixes, AND/OR grouping, quoted literals,
  single-character wildcards, attachment counts, flexible dates, and in-product syntax help.
- Added an explicit Tools command to rebuild message and attachment search indexes while retaining
  reusable SHA-256 extraction caches.
- Added background full-text indexing for TXT/CSV/HTML/XML/JSON, DOCX/XLSX/PPTX and text PDFs,
  including documents streamed from ZIP/RAR/7z/TAR/GZip containers. Input and expanded-size limits
  are configurable and archive depth, entry count, encryption and expansion are guarded. SHA-256
  and extracted-text caches reuse unchanged or duplicate files across Eudora reimports.

### Composition and rendering

- Embedded HugeRTE as the HTML compose editor and preserved received HTML when replying or
  forwarding.
- Improved reading-pane HTML/CSS rendering, external images, Eudora-linked inline images, attachment
  interaction, and safe source viewing.
- Added sent-recipient address autocomplete, visible full recipient addresses, HTML as the default
  compose mode, and correct AltGr handling on European keyboards.
- Added selectable Spanish, Catalan, and English spelling languages with live underlining, native
  contextual suggestions, ignore/add-to-dictionary actions, and draft language metadata.
- Added `(None)` to disable proofing, forced dictionary reloads when changing language, and one-shot
  Spanish/Catalan/English detection after the first five words of a new message.
- Isolated the HTML editor into per-language WebView2 profiles so selecting a proofing language
  changes Chromium's dictionary and context menu instead of merely changing the HTML `lang` tag.
- Added reliable offline Catalan proofing with Softcatalà's Hunspell dictionary, wavy error
  underlining, contextual suggestions, and integration with QuickMail's personal dictionary.
- Preserved the exact caret/selection offsets while Catalan proofing reshapes HTML text nodes, so
  live underlining no longer moves the insertion point backwards while typing.
- Added selected-text translation while composing, with a local Argos Translate provider and an
  optional DeepL provider. Provider configuration includes setup/testing, local model management,
  a persistent default, privacy guidance, and Windows DPAPI protection for the DeepL API key.
- Added `Translate → English / Spanish / Catalan` to the HTML compose contextual menu; it uses the
  active correction language as the source and replaces only the selected text.
- Added an on-demand `Grammar Check` action for selected compose text in Spanish, Catalan, and
  English. It runs LanguageTool entirely on localhost, presents its issues and suggested corrected
  text for review, and replaces only the selection after confirmation. The approximately 252 MB
  LanguageTool component is downloaded once into the active profile after explicit consent.
- Grammar review now supports selecting multiple issues, choosing among alternative replacements,
  applying only the selected corrections, or applying every correction in one action.
- New-message windows now enter HTML mode before their XAML is displayed, avoiding the visible
  Plain Text → HTML editor swap; their initial size is larger to leave useful body-editing space.
- Fixed direct-HTML startup to bootstrap HugeRTE with an empty document instead of exposing its
  narrow fallback textarea, restoring language detection, disabled proofing for `(None)`, the
  English QuickMail contextual actions, full editor width, and content preservation across reloads.
- Automatic language detection now activates the matching Chromium proofing dictionary after a
  short typing pause, preserving both the HTML content and caret while the editor profile changes.
- Improved short-text language detection with weighted language-specific vocabulary, avoiding
  shared words such as `a` and `una` overriding clear Catalan phrases such as `Anem a fer una prova`.
- Fixed asynchronous draft-close re-entry and other composition focus/keyboard issues.

### Performance and reliability

- Added targeted SQLite indexes and FTS row-key tracking for fast bulk deletion and search.
- Added high-volume import/search diagnostics and optimized message paging for large local archives.
- Published self-contained Windows builds so users do not need to install a separate .NET runtime.
- Added a native WPF `Microsoft 365 Blue` theme modelled on Krypton Suite's
  `PaletteMode.Microsoft365Blue`, without introducing WinForms interop into the interface.

## Attribution

- QuickMail original project: Copyright (c) 2026 Kelly Ford, licensed under MIT.
- HugeRTE is redistributed under its own license in `QuickMail/Assets/HugeRte`.
- The Catalan Hunspell dictionary is from Softcatalà's `catalan-dict-tools` v3.0.9 and is
  redistributed under its LGPL-2.1/GPL-2.0 dual license in `QuickMail/Assets/Dictionaries/Catalan`.
- WeCantSpell.Hunspell is used to read that dictionary; its Hunspell tri-license notice is shipped
  in the same directory.

No user mail databases, credentials, logs, generated executables, or local profiles belong in Git.
