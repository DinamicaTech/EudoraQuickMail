# Eudora QuickMail 0.8.72

This pre-release updates the DinamicaTech edition with safer message recovery, faster ways to
trace correspondence, refreshed application branding and a simpler Windows download.

## Highlights

- `Shift+Delete` now moves messages to a dedicated **RecoveryDeleted** folder instead of deleting
  them immediately. Its configurable retention period defaults to one day, and Trash, Junk and
  RecoveryDeleted can each be emptied explicitly from the folder context menu.
- Message history can now search either an exact email address or an entire domain. **Go to
  message** opens a result in its physical folder, and longer local searches display progress.
- Compose windows and tabs support `Ctrl+F4`, alongside refinements to notification handling,
  scheduled sending, local search and folder-state recovery.
- The application, splash screen, taskbar and notification-area artwork now use the new Eudora
  QuickMail paper-plane icon at appropriate resolutions.
- The portable application is now named `EudoraQM.exe`. The x64 release also publishes a stable
  `EudoraQM-Setup.exe` asset for a direct installer link from the product website.
- Documentation and automated tests were expanded for the new workflows.

## Installation note

This build is temporarily unsigned while the project completes onboarding to SignPath Foundation.
Windows may display an "Unknown publisher" warning. Back up important mail data before installing
or migrating an existing profile.

For setup, migration and usage instructions, see [USERGUIDE.md](../USERGUIDE.md).
