# Eudora QuickMail 0.8.77 — preview

Official GitHub build with the configured Google OAuth desktop client.

- Includes Dinamica Backup Agent 0.1.3: native ZIP backups, external maintenance
  windows, native attachment mirror, restore tests and recovery workflow.
- Backup settings grouped by application, external/native backup, notifications,
  restore tests and history, with tooltips and independent destination credentials.
- Clear SQLite preparation stages with elapsed time; separate sender email/display
  name; dirty-state Save button; Windows task identity resolution and registration feedback.
- Backup report durations have no fractional seconds. Dedicated application icon.
- Includes pending recipient-validation improvements and safe EQM shutdown coordination.

For a portable installation, use the complete ZIP for your architecture and preserve
its Assets and BackupAgent directories. The loose backup executable is x64.
Close the backup agent before replacing its executable. Do not replace files during
an active backup. Back up your existing data before testing recovery.

This is a pre-release for manual testing, not promoted to Latest or automatic updates.
ProConta integration remains pending. Native backups currently keep EQM closed for
the whole operation; this release does not change that lifecycle.
