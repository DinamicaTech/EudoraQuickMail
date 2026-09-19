# Eudora QuickMail 0.8.71

This is the first public DinamicaTech build of the Eudora QuickMail fork. It is published as a
**pre-release** for evaluation and is temporarily **unsigned** while the project completes
onboarding to SignPath Foundation. Windows may display an "Unknown publisher" warning.

Do not treat this build as the stable update channel yet. Back up important mail data before
installing or migrating an existing profile.

## Highlights

- Local-first mail for IMAP/SMTP, POP3/SMTP, Microsoft Graph and archive-only accounts, with
  unified folders and fast SQLite full-text search.
- A comprehensive Eudora migration path for mailbox hierarchies, messages, attachments, account
  settings and supported filters.
- Durable outgoing queue with Send Later, configurable Undo Send delay, retry handling and
  crash-safe SMTP delivery checkpoints.
- Message Snooze, Focus and Work Offline modes, saved searches as virtual folders, rules preview,
  Account Health and EML/HTML export.
- Forgotten-attachment detection, subscription controls, rich HTML composition, templates,
  spelling, local grammar checks and optional translation.
- Calendar, contacts, ICS invitation handling and Google Calendar integration.
- Accessibility-focused keyboard navigation, screen-reader labelling, themes and reader tools.
- Native self-contained Windows x64 and ARM64 packages plus portable executables.

## Notes for this pre-release

- The code-signing configuration belonging to the upstream project has been removed. A future
  pre-release will integrate a DinamicaTech-owned SignPath Foundation identity.
- Automatic updates remain protected because this release is marked as a pre-release.
- OAuth and bug-report relay credentials are injected only from repository secrets during CI and
  are not stored in source control.

For the complete development history, see [FORK_CHANGELOG.md](../FORK_CHANGELOG.md). For setup,
migration and usage instructions, see [USERGUIDE.md](../USERGUIDE.md).
