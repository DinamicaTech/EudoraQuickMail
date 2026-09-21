# QuickMail fork development log

This file records the functionality developed on top of the original
[QuickMail](https://github.com/kellylford/QuickMail) project. It complements the upstream
`CHANGELOG.md`; upstream authorship and the original MIT license are preserved.

The first entry is a consolidation of the prototype work completed before the fork began using
incremental commits. Subsequent work should be added as normal Git commits and summarized here
before a public release.

## Unreleased

- Added a first-run and File-menu QuickMail profile importer. It snapshots `mail.db` through
  SQLite's backup API (including WAL state), validates the source schema and database integrity,
  copies supported profile data into an independent destination, preserves locally stored POP3
  MIME/attachment bytes and remote attachment metadata, and starts the imported profile offline by
  default. The source profile is never modified and a running QuickMail instance is rejected.
- Added message Snooze as a visible unified system folder. Snoozed mail is hidden from its
  original folder but remains searchable; at the selected preset or custom local date/time it
  returns to that folder as unread. The local metadata works for POP/archive and IMAP accounts.
- Reading preview detects standards-based and visible HTTP unsubscribe links. It offers a prominent
  Unsubscribe action in an ephemeral, permission/download-disabled WebView2 profile, plus a
  persistent per-sender Maintain subscription suppression.
- Compose warns before Send or Send Later when the new text mentions an attachment in English,
  Spanish or Catalan but no file is attached, while ignoring common quoted-message history.
- Added persistent Work Offline and timed Focus modes. Offline pauses every sender and receiver;
  Focus pauses receiving only. Check Mail returns the application online before running.
- Today's compact agenda renders each appointment as a full-width green pill so events remain
  visibly distinct from the surrounding folder/navigation chrome.
- Quick search now exposes **Save as Virtual Folder…** directly beside the search actions. Creation
  remains explicit and the resulting search appears under the existing Views node instead of
  accumulating automatically for ad-hoc searches.
- Account Health now sizes every diagnostic column to the available window width and marks accounts
  requiring attention with the theme's red error background.
- The reading-pane contextual menu now includes the same View Source and Export (EML, HTML and
  headers) actions as the message-list contextual menu.
- Plain-text templates inserted into the HTML composer retain their line and paragraph breaks.
- Compose now presents one split Send control: normal Send remains the primary click, while the
  dropdown offers Send later, Save as Draft and Send immediately (which bypasses Undo Send delay).
  Template actions moved beside Add Files, and scheduling a message before today shows Eudora's
  original time-machine warning.
- Saved views can now retain an active quick-search expression and its folder/global scope, turning
  the existing Views tree into lightweight smart folders without a second persistence format.
- Rules Manager now offers a read-only **Preview** over the current folder (and Out when configured),
  reporting scanned/matched/incoming/outgoing/mark-read counts and showing the first 20 matches
  before the operator commits any move or state change.
- Added **Tools > Account Health**: one refreshable overview of active state, protocol/server
  configuration, silent OAuth or saved-password availability, current-session last download and
  Scheduled queue errors for every account.
- The message-list contextual menu can export one or several messages as portable `.eml` files
  (including available attachments), readable `.html` documents, or copy their original headers.
- Added configurable **Delay sending messages** (0–600 seconds). Zero sends directly through the
  transport; a positive delay persists the message in Scheduled and displays a highlighted
  `Undo · subject` action beside Forward until dispatch claims it. Undo atomically cancels the
  queued item and restores its latest content to Draft; dismissing the notice does not cancel it.
- Normal **Send** now uses the durable Scheduled outbox and wakes its dispatcher immediately, so
  SMTP/OAuth latency no longer blocks the compose window or the rest of QuickMail. The complete
  message is persisted before the editor closes, concurrent sends can be queued while transport is
  busy, transient errors use bounded retry backoff, and permanent credential/configuration errors
  wait for manual retry. SMTP acceptance is checkpointed before Sent-copy and draft housekeeping to
  prevent duplicate delivery after a crash or shutdown.
- Sending from a POP3 account added during the current session now creates its physical Sent folder
  and binds it to the shared canonical Out node before saving the local sent copy. SMTP could
  previously succeed while the copy was lost with `This local account has no Sent folder`.
- Dragging PDF, ZIP, Office or other non-image files onto the HTML composer now intercepts the
  native drop during WebView2's capture phase, before HugeRTE can reject it as unsupported inline
  content. The editor resource is cache-versioned so upgrades cannot retain the old drop bridge;
  files above the 25 MB recommendation remain allowed with a warning.
- Account management now offers **Duplicate account**. It opens a new-account editor seeded with
  the selected account's provider, protocol, servers, ports, certificate policy, signature and
  folder configuration, while clearing email/login names and all password/OAuth/calendar identity
  data; the copy receives a fresh id and can never inherit the default-account flag.
- **Check incoming email** is now visible and editable for every server-backed account (IMAP,
  POP3 and Graph) in the add and account-management dialogs. The setting was already enforced by
  mail checking for IMAP/Graph, but its checkbox had accidentally remained inside the POP3-only
  visual panel; local archive accounts continue to hide it.
- **Filter All Like This** now applies a matching rule independently to each physical
  account/folder source in a unified view. A revoked OAuth token or other remote failure can no
  longer conceal successful local moves or prevent the message list from refreshing; partial
  completion is reported with the affected account and an actionable Google relink hint.
- Filtering the selected message with `Shift+F` or **Filter All Like This** now opens a prefilled
  new-rule editor immediately when no enabled rule is compatible with that message, instead of
  silently reporting zero matches.
- IMAP summaries without a cached body are now completed by a cancellable background indexer.
  Its SQLite queue survives restarts, distinguishes downloaded/empty/error states, retries failures,
  uses non-`Seen` body reads, and collapses Gmail label copies by RFC Message-ID so one download can
  populate every physical copy without changing unread state.
- Added **Search Everywhere** beside quick search. It runs or reruns the current query across every
  active non-shared account without changing the selected folder, while retaining the root-search
  exclusion of non-materialized Draft and Scheduled items.
- Folder paths now trim leading/trailing whitespace from every segment at creation and Eudora
  import. Startup repairs older local POP/archive paths transactionally across folders, messages,
  full-text/attachment indexes and calendar references, merges clean-name collisions, and rewrites
  affected rule, startup, recent-destination and saved-view references. Existing IMAP server aliases
  are preserved. Root views also include active accounts proven by canonical bindings, preventing
  moved IMAP messages from disappearing when an older account record lacks its shared-root id.
- Incoming iCalendar content is now handled consistently across POP3, IMAP and Graph: both
  `text/calendar` parts and generically typed `.ics` attachments are recognized, the raw ICS is
  cached and remains visible/downloadable as an attachment, `METHOD:REQUEST` keeps its RSVP
  controls, and standalone publications offer an explicit **Add to Calendar** editor instead of
  being silently harvested. POP3 parses calendar data before materializing/consuming its MIME
  stream, and messages cached by the affected interim build repair `calendar_ics` automatically
  from their safely materialized `.ics` file when opened.
- IMAP special folders excluded from the All Mail view are now still synchronized: opening the
  canonical Out folder refreshes each connected account's real Sent folder, periodic/startup-wide
  sweeps no longer mistake `ExcludeFromAllMail` for `DoNotSync`, and successful immediate or
  scheduled sends refresh Out. A scheduled SMTP failure remains safely queued in Scheduled with
  its error/retry state rather than being downgraded to Draft; successful scheduled mail is moved
  to Sent without risking a duplicate SMTP retry when saving the Sent copy fails.
- Added `AI_HANDOFF.md`, a self-contained continuation specification covering the fork's
  complete functional inventory, architecture, persistent-data and folder invariants, Eudora
  migration contract, search/rule grammar, build/installer workflow, regression checklist and
  safe instructions for a future developer or AI working without the original conversation.
- Rule filtering from the message list now preserves the operator's place after its local refresh:
  focus lands on the first surviving message at the filtered row (or the preceding final row),
  matching Delete and Shift+Delete instead of jumping to the top of the grid.
- Quick search now keeps a persistent, deduplicated ten-item history in an editable drop-down;
  selecting a previous query never runs it until Search or Enter is pressed.
- Added Tools > Analyze Folder, which asynchronously reports the twenty most frequent sender
  domains across the selected folder's complete recursive/local-index scope rather than only the
  currently rendered message page.
- Added Tools > Delete Orphaned Attachments: it safely compares physical files in the applicable
  Eudora `Attach*` or QuickMail Attachments tree with live database references, presents a sortable
  path/date/size grid, supports guarded double-click opening, clipboard export, and confirmed
  selected/all deletion without traversing or deleting filesystem links.
- Eudora account migration now decodes valid `SavePasswordText` values and immediately re-protects
  them with current-user Windows DPAPI. A new opt-in migration setting can respect each persona's
  `CheckMailByDefault`; it defaults off so imported accounts cannot fetch mail before review.
- Moved Detach Preview to the upper-right of the reading-pane header and added local in-place
  message editing on double-click: received and sent mail can change only Subject and Body through
  the existing rich composer, with envelope fields locked, only Save/Cancel actions exposed, and
  the summary, preview and full-text index updated atomically without altering attachments.
- The Eudora migration wizard can now optionally import `filters.pce` (enabled by default),
  converting supported contains-and-move filters into global QuickMail rules while preserving
  automatic/manual behavior, resolving Eudora folder paths, retaining existing rules, avoiding
  duplicates on repeated imports, and reporting unsupported rules in the migration summary.
- Installed copies now honor their adjacent `QuickMailDataPath.txt` even when the executable is
  launched directly without shortcut arguments; About displays the active data folder explicitly.
- Added actionable error dialogs for manual receive/send failures, identifying the account,
  operation and server and suggesting credential, connectivity, port, encryption or certificate
  corrections; multi-account Check Mail failures are consolidated into one report.
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
- Added FullCalendar appointment actions to duplicate an event into a fresh editable copy and to
  share it by opening a new message with an RFC-compatible ICS attachment.
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
- Made Catalan contextual replacements apply on the first click as a single undoable edit, and
  replaced fragile cross-paragraph text offsets with DOM boundary markers so Enter no longer
  returns the caret to the previous paragraph during live proofing.
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
