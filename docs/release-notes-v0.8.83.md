# Eudora QuickMail 0.8.83

- Help now checks for updates on demand without restarting, distinguishes errors
  from successful checks, and explains disabled test installations. Available
  updates open the in-app download/install dialog; GitHub has a separate link.
- Outgoing embedded images are stored durably in Data/Embedded and sent as inline
  MIME resources. Drafts, scheduled messages, sent copies and forwards retain
  them. Native backups include Embedded when attachment copying is enabled.
- Past events are dimmed in the monthly calendar without changing other views.
- Searches can open in dedicated result tabs (enabled by default), preserving
  the original folder view, selection and scroll position.
- Slow folder navigation shows a delayed progress window.
- Startup during backup offers safe cancellation/waiting rather than an opaque
  database-lock error; it never forcibly terminates backup or bypasses its locks.
- Cursor visibility is restored on window activation. Copying a missing
  attachment preserves its original path for recovery from backups.

## Installation and updates

Official x64 and ARM64 packages include Google OAuth credentials, Backup Agent,
Updater and application assets. Close EQM and Backup Agent before replacing files;
do not update during a running backup. For portable installations, extract the
complete ZIP for your architecture, preserving Data and profile directories.

If Help reports "Updates disabled", an old `quickmail-standalone-install` marker
is present beside EudoraQM.exe. Rename it to `quickmail-standalone-install.disabled`
if this installation should receive official releases, then use Help > Check for
updates now. Installing new files alone does not remove this old marker.

Updates require confirmation before installation and restart. Only newer stable
GitHub releases are offered; a locally built version ahead of GitHub is not downgraded.
