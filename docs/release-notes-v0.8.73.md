# Eudora QuickMail 0.8.73

This release adds a safe migration path for users of the original QuickMail project, improves
attachment storage and local-database maintenance, and refines the Eudora QuickMail Windows icon.

## Highlights

- Existing QuickMail profiles can be imported during first run or later from **File → Import
  QuickMail Profile…**. The importer creates an independent, validated copy, leaves the source
  untouched, and opens the result offline for review.
- SQLite profiles are copied through the online backup API so committed WAL data is included.
  Compatible settings, contacts, rules, templates, themes, locally retained attachments, POP3 MIME
  data, and remote attachment metadata are preserved.
- Eudora migration remains available during first run and from the File menu.
- Locally retained received, embedded, and migrated attachments are maintained as independent files
  below the profile's `Attachments` tree, with references and metadata held in SQLite.
- Database schema 9 removes orphaned search keys, attachment-index rows, and IMAP body-cache state
  left by older deletion paths without forcing a large full-text-index rebuild during startup.
- Small Windows icon frames have been optically resized for clearer taskbar, title-bar, and
  notification-area presentation.
- The installer keeps the stable asset name `EudoraQM-Setup.exe`. Once this release is promoted,
  the permanent URL `/releases/latest/download/EudoraQM-Setup.exe` follows it automatically.

## Installation note

The build is temporarily unsigned while the project completes onboarding to SignPath Foundation.
Windows may display an "Unknown publisher" warning. Back up important mail data before installing
or migrating an existing profile.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/). See the project's
[release-signing policy](SIGNING.md) for its build provenance, approval roles and privacy policy.

For setup, migration and usage instructions, see [USERGUIDE.md](../USERGUIDE.md).
