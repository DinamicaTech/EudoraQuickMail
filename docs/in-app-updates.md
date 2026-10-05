# In-app application updates

Implemented after 0.8.77; not present in the already-published 0.8.77 binary.

- Help's available-update entry opens **Download and install**, with progress and cancellation.
- Installed Velopack copies retain their architecture/channel/feed. AutoUpdate controls background
  downloading; an explicit manual installation remains available when it is off.
- Portable copies select the official `EudoraQM-vVERSION-win-x64.zip` or `win-arm64.zip` according
  to the running process architecture. Only the fixed official repository URL is accepted. Both
  exact size and the SHA-256 digest published by GitHub are checked before extraction.
- Missing/legacy ZIPs or missing digests retain the release-page fallback, rather than installing
  an incomplete/unverified EXE. First adoption from a portable 0.8.77 requires the complete next
  package, including `Updater`, once. Later releases can update themselves from inside EQM.
- Downloading never closes EQM. After preparation, ask permission, pause new mail activity, wait
  up to five minutes for active operations, save open composition drafts, then arm the updater and
  use normal application shutdown. Cancellation/failure restores activity without altering the
  persisted Offline/Focus setting. Open settings dialogs and Backup Agent must be closed first.
- A pending backup request prevents installing. Downloads are NOT applied implicitly when EQM
  exits (in particular, never during backup shutdown). This deliberately replaces old Velopack
  apply-on-exit behaviour. The normal next-start version/what's-new detection remains unchanged.
- The portable helper is self-contained and published in `Updater`. A copy runs outside the
  installation, waits for the exact parent PID/start time and an explicit commit marker, and never
  kills it. An installation lock blocks another launch while files are replaced. Other running
  processes from the installation prevent replacement. Restricted/read-only installations require
  manual administrator installation; the helper does not silently elevate.
- Only EudoraQM binaries/debug metadata, Assets, BackupAgent and Updater files are accepted. No
  Data, configuration, profile-pointer files or arbitrary root files can be in an update. Traversal,
  ADS, duplicate paths and reparse points are rejected. Existing files not shipped by the update
  are left intact. All originals are saved before replacement, and write errors roll back changed
  files. Data is never migrated by the helper; the application retains its normal startup migrations.
- Work files, result.log and previous binaries are retained under
  `%LOCALAPPDATA%/EudoraQuickMail/Updates/<version>-<unique id>/`. This is not a mail backup. Space
  is checked before preparation; failed/cancelled preparation removes its payload files. Confirmed
  stages/previous binaries remain for diagnosis/manual recovery; no automatic pruning yet.
- Normal success reopens the original EXE with the original arguments and working directory.
  The helper reports errors in a dialog and result.log instead of restarting a potentially mixed
  installation. Power-loss/crash recovery mid-replacement still requires restoring the retained
  previous files or reinstalling the complete package; this is file-level rollback, not an atomic
  whole-directory deployment.

Validation: `PortableUpdateTests` uses synthetic files only: preservation, rollback, incomplete
packages, archive traversal and architecture/digest selection. Publish the helper for both RIDs.
Before promoting the first release, exercise an actual N → N+1 update on a disposable portable
profile and a Velopack installation (including cancellation and a running backup agent).

Local validation on 2026-09-27: 3,180 EQM tests passed (including 20 portable-update cases),
Release build with warnings-as-errors passed, and self-contained x64/ARM64 helper publishing
passed. `scripts/Test-PortableUpdater.ps1` also passed a detached-helper smoke test using a
dummy process: wait for exit, replace assets, preserve synthetic Data, retain previous files,
and restart with the requested working directory/arguments. This is not a production
Google-OAuth release or a completed installed-Velopack N → N+1 acceptance test.
