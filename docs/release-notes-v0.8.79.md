# Eudora QuickMail 0.8.79

This release includes the changes validated in the local 0.8.79 test package.

- Stable message results: applying rules or deleting messages keeps the remaining
  rows, order and reading position. Read-state changes no longer remove rows from
  an open unread search; F5 or a new search reevaluates its criteria.
- New mail is added incrementally, respecting the selected order and viewport.
- Snooze presets for 30, 90 and 180 days and one calendar year. Date-only input
  in custom Snooze and Send Later defaults to 09:00 local time; explicit times
  are preserved.
- In-app download and installation for supported installed and portable copies,
  including package verification, orderly shutdown and the portable updater.
- Manual SPAM blocking, improved rule diagnostics and IMAP filter performance,
  extended wildcard search, and read-state handling for RecoveryDeleted.
- Backup Agent improvements: independent destination-access checks, manual task
  requests, legacy Eudora attachments, external backup window naming, history
  access and reopening EQM in the user's interactive session after maintenance.

The release includes x64 and ARM64 portable ZIPs, installers and update feeds.
Official builds use the Google OAuth client configured in GitHub Actions.

For a manual portable update, close EQM and replace the application files using
the complete ZIP for your architecture, including Assets, BackupAgent and Updater.
Keep your existing data and profile directories. Close Backup Agent before replacing
its files and do not update during a running backup.

Portable 0.8.77 predates the in-app installer and still opens GitHub when an update
is found. Install the complete package once to enable subsequent in-app updates.
A client already running 0.8.79 will not offer this same version again.
