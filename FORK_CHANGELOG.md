# QuickMail fork development log

This file records the functionality developed on top of the original
[QuickMail](https://github.com/kellylford/QuickMail) project. It complements the upstream
`CHANGELOG.md`; upstream authorship and the original MIT license are preserved.

The first entry is a consolidation of the prototype work completed before the fork began using
incremental commits. Subsequent work should be added as normal Git commits and summarized here
before a public release.

## Unreleased

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

### Composition and rendering

- Embedded HugeRTE as the HTML compose editor and preserved received HTML when replying or
  forwarding.
- Improved reading-pane HTML/CSS rendering, external images, Eudora-linked inline images, attachment
  interaction, and safe source viewing.
- Added sent-recipient address autocomplete, visible full recipient addresses, HTML as the default
  compose mode, and correct AltGr handling on European keyboards.
- Added selectable Spanish, Catalan, and English spelling languages with live underlining, native
  contextual suggestions, ignore/add-to-dictionary actions, and draft language metadata.
- Added selected-text translation while composing, with a local Argos Translate provider and an
  optional DeepL provider. Provider configuration includes setup/testing, local model management,
  a persistent default, privacy guidance, and Windows DPAPI protection for the DeepL API key.
- Fixed asynchronous draft-close re-entry and other composition focus/keyboard issues.

### Performance and reliability

- Added targeted SQLite indexes and FTS row-key tracking for fast bulk deletion and search.
- Added high-volume import/search diagnostics and optimized message paging for large local archives.
- Published self-contained Windows builds so users do not need to install a separate .NET runtime.

## Attribution

- QuickMail original project: Copyright (c) 2026 Kelly Ford, licensed under MIT.
- HugeRTE is redistributed under its own license in `QuickMail/Assets/HugeRte`.

No user mail databases, credentials, logs, generated executables, or local profiles belong in Git.
