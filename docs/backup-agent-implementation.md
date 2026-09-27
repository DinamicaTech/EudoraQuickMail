# Dinamica Backup Agent

Implemented 2026-09-27 in `C:\Docs\Codigo\QuickMail`. Development must never use a
production profile for tests or install scheduled tasks automatically.

## Product contract

- Separate reusable executable, launched by EQM through **Tools → Backup and Restore**.
- Full independent ZIP copies of SQLite and explicitly listed profile configuration files.
  No incremental database chains, no VACUUM, no production schema changes.
- One native attachment mirror, including embedded resources under `Attachments/Embedded`.
  Only changed/new files are recopied, using recorded path, size, timestamp and SHA-256.
  Newly copied files are read back and hashed before replacing the destination file.
- Optional deletion of missing attachments. No historical attachment versions: restoring an
  old DB can encounter missing/changed attachments. Warn in configuration and restoration.
  A missing source directory or removal percentage above the configured threshold prevents
  propagation of deletions. Only files in the agent's prior inventory may be deleted.
- Configurable representative daily/weekly/monthly retention. Keep at least one completed
  ZIP, and prune only after verifying and publishing a newer ZIP.
- Separate native-backup, external-maintenance and restore-test schedules. EQM stays closed
  for the complete backup and any overlapping external window. External backup success is
  explicitly unknown; the agent only guarantees its own configured maintenance interval.
- Scheduled executions run through Windows Task Scheduler (five-minute polling). Daily,
  weekly and monthly schedules, missed-run catch-up, history of missed occurrences, no overlap.
- ZIP extraction/hash/SQLite verification, configurable scalar SELECT probes captured at
  backup time and compared exactly after restore. `quick_check` and `foreign_key_check` by
  default, optional full `integrity_check`. SQL probes are read-only, cancellable and limited
  to 30 seconds of query execution via SQLite's progress handler.
- Restore tests have their own target directory and schedule. Select yesterday's latest
  successful backup or the latest backup. Keep failed restorations; optionally retain successful
  ones. Sample X attachments from the expected inventory, mixing directory-distributed random
  choices and least-recently verified paths. Missing files are therefore eligible for detection.
- Real restore: DB only, DB + attachments, attachments only, selected attachments. Profile JSON/INI
  restoration is separately selectable. Explicit overwrite confirmation. Stage and verify the
  archive before replacing live files. Retain replaced files in a recovery directory.
- Progress and safe cancellation. Interactive scheduled execution displays a small progress
  window. Without an interactive session, the task runs headlessly. Reopen only an application
  the agent closed; do not launch an interactive application in session 0.
- Independent Windows-task and NAS identities. Windows Credential Manager protects NAS/SMTP
  secrets for the account executing the task. No passwords in JSON, logs or command arguments.
- Optional before, after-success and finally PowerShell commands, with timeout and environment
  variables `DINAMICA_BACKUP_PROFILE` and `DINAMICA_BACKUP_DESTINATION`.
- SMTP reports with the last 31 recorded operations, colored status squares, start, duration,
  file count and warnings. Notification failure is separate from backup success. Destination
  connection failures also retain a local receipt and can generate SMTP failure reports.

## Components and packaging

- `DinamicaBackup.Core`: filesystem/ZIP/SQLite engine, manifests, catalogue, profile lease,
  schedules, restore/recovery, scripts and notifications. No dependency on EQM's schema.
- `DinamicaBackupAgent`: WPF configuration, restore picker, progress windows, Windows credentials,
  NAS session connection and task registration.
- `DinamicaBackup.Tests`: isolated synthetic SQLite and attachment regression tests.
- `QuickMail/Views/MainWindow.Backup.cs`: EQM UI integration and coordinated shutdown.
- `QuickMail/Services/BackupOperationTracker.cs`: send/sync work that must finish before exit.

The EQM `PublishBackupAgent` MSBuild target packages a self-contained agent matching the
main application's runtime identifier into `<publish>/BackupAgent/`. Local publishing and
existing installer/release workflows therefore include the helper. The helper is not a Windows
service. A second application must implement the profile cooperation protocol below; merely
setting its executable path does not make an unmodified application safe to back up while live.

```
DinamicaBackupAgent.exe /config /job "C:\Example\Data\backup-job.json"
DinamicaBackupAgent.exe /scheduled /job "C:\Example\Data\backup-job.json"
```

`/config` can create a new job when the JSON file does not exist. **Save configuration** does
not register a Windows task. **Register / Update Windows task** is explicit. Blank Windows
identity registers an interactive-user task; an explicit Windows username/password allows
unattended execution. NAS/SMTP credentials must be provisioned under that execution identity.

## Stored data

- Profile `backup-job.json`: job ID, paths, schedules, retention, SQL probes, credential names,
  SMTP parameters and scripts. Secrets are external. Defaults have all schedules disabled.
- ZIP `manifest.json`: format/job/backup IDs, source path, captured configuration, database
  schema version, file sizes/hashes, SQL definitions/results and attachment inventory. ZIP
  entries under `profile/` contain the DB and selected profile files. ZIP64 is supported by .NET.
- ZIP destination `.dinamica-backup/<jobId>/backup-catalog.db`: independent SQLite catalogue,
  receipts, sample-verification history. Uses no WAL on a NAS. Rebuilds from JSON receipts;
  if receipts also disappeared, ZIP manifests can recover a basic archive listing.
- Attachment destination `.backup-owner` and `.attachment-manifest.json`: repository ownership
  and current file inventory. First use requires an empty destination to avoid adopting user data.
- Profile `.backup-history/<jobId>/`: local run receipts, including failures when NAS is unavailable.
  `.backup-schedule-state.json`: handled occurrences; `backup-agent-last-run.json`: latest outcome.
- `.restore-staging-<runId>/` and `.restore-previous-<runId>/`: validated staging and replaced files.
  `.restore-in-progress.json` blocks EQM startup after interruption. The recovery action rolls back
  from the journal and retains partially restored files with an `.interrupted-<runId>` suffix.
- `.backup-restored-profile.json`: successful DB restore marker. EQM starts offline once,
  asks the user to review accounts/Scheduled, and persists Offline mode until explicitly cleared.
  Restoring an old queue must not silently resend messages already sent after that backup.

## Coordination protocol

1. EQM opens `.backup-access.lock` for read with shared-read access before opening SQLite and
   holds it for its lifetime. It refuses startup while a valid request, exclusive lease or
   incomplete restore journal is present.
2. Agent owns `.backup-agent.lock` exclusively for the job, preflights the destination and
   writes `.backup-request.json` containing an unpredictable ID and expiry (default five minutes).
3. EQM pauses new mail reception/queued dispatch without changing the persisted online mode.
   It disables its main/compose surfaces, waits for active SMTP/POP3/sync and busy UI work,
   flushes HTML editor content and autosaves drafts. Unsavable edits or open settings/editor
   dialogs defer shutdown until timeout rather than losing work.
4. EQM writes `.backup-response.json` with request ID, PID and process start ticks, then exits.
5. Agent waits for that exact process to exit and obtains the exclusive profile lease.
   It does not force-kill EQM. A destination operation lock also serializes agents sharing a NAS.
6. SQLite performs a checked WAL checkpoint and closes the connection. Database reads are
   shared-read only while compressing. Partial ZIPs never appear as completed backups.
7. When finished/cancelled, release the profile lease and reopen only the application that
   acknowledged this request, in an appropriate interactive session. Timeouts withdraw the
   request and allow EQM to resume. Crashed agents release OS locks automatically; interrupted
   restores remain blocked by their persistent recovery journal.

## Scope and operational limits

- The database engine currently supports SQLite. ProConta can reuse the UI/engine if it uses
  SQLite and implements the cooperation protocol; another DB engine needs an adapter.
- Attachment sources are configured as paths relative to the application profile. Files linked
  elsewhere (such as Eudora's untouched Attach directories or arbitrary sent attachments) are
  not silently copied. Configure a suitable source layout or back those locations up externally.
- A relocated SQLite file can still contain absolute attachment paths. Restoring files into a
  test/alternative directory verifies their bytes, but does not rewrite application references.
- Sampling checks the selected files, not every attachment. Historical mismatches are warnings
  because the native mirror intentionally has no versions. Configure sample size accordingly.
- The compressed database ZIP is not encrypted. Destination access controls protect its contents.
  Protected application credentials may require reauthorization on a different Windows account.
- ZIP compression plus read-back verification and SQLite checks perform several reads. Production
  timing on a 20+ GB database/NAS still needs measurement; tests use synthetic data.
- Failed test/staging directories and `.restore-previous-*` recovery copies are retained for
  inspection and consume space. Their removal is not part of archive retention.
- Task scheduling, real NAS credentials and SMTP delivery require validation in the deployment
  environment. Development does not register tasks, close production EQM, or touch live mail data.

## Verification

### Configuration UI revision

The UI now has Application, External Backup, Native Backup, Notifications, Restore test and
History tabs. Native Backup's first checkbox enables its settings and schedule independently
of external maintenance. External-only configurations do not require a ZIP destination.
History fills its tab instead of sitting inside an unconstrained scrolling StackPanel.

EQM detection uses the live profile supplied by EQM when selecting the same installation,
otherwise QuickMailDataPath.txt or EQM's documented AppData default. Advanced overrides remain
available. The default scalar probe reads MAX(date_ticks) from MessageSummary; existing custom
probes are preserved. This compares source and restored values, not backup freshness.
ProConta is a clearly marked placeholder, with execution blocked pending its future integration.

Database NAS credentials retain the legacy NasShare/NasCredential fields. Attachments have
independent AttachmentNasShare/AttachmentNasCredential fields. Credentials remain protected
by Windows Credential Manager; Windows may reject distinct identities on shares of one host.
The Test email button sends a real test using the form settings without running a backup,
closing EQM, requiring a backup destination, or requiring scheduled reports to be enabled.
No actual SMTP messages or NAS connections are performed by the automated configuration tests.

UI verification: dotnet test Tools/DinamicaBackupAgent.Tests/DinamicaBackupAgent.Tests.csproj
checks tab order, enabling, preserved probes, destination buttons and responsive History layout.

```
dotnet test Tools/DinamicaBackup.Tests/DinamicaBackup.Tests.csproj
dotnet build Tools/DinamicaBackupAgent/DinamicaBackupAgent.csproj
dotnet test QuickMail.Tests/QuickMail.Tests.csproj --filter "FullyQualifiedName~ComposeViewModelAutoSaveTests|FullyQualifiedName~SyncService|FullyQualifiedName~Scheduled|FullyQualifiedName~MailActivity|FullyQualifiedName~XamlParseTests"
```

The initial verification passed 28 backup-engine tests and 103 existing EQM tests. These include
archive/restore round trips, corruption, individual attachment restoration, missing sources,
mass-removal guards, cancellation, source locking, retention, scalar queries, foreign-key
violations, unavailable destination, schedule calculations, catalogue reconstruction and
interrupted-restore rollback. This is not a claim that the entire EQM suite was run.
