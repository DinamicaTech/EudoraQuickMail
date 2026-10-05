# Local builds with the existing Google OAuth client

No GitHub upload/release is required. Obtain the JSON of the **existing official Desktop OAuth client** from its administrator/Google Cloud, not a newly created client. GitHub Actions secrets cannot be downloaded through the CLI.

Run PowerShell as the Windows user who will compile:

```powershell
./scripts/Local-OAuthBuild.ps1 -Action Import -ClientJson 'C:\secure\official-client.json'
./scripts/Local-OAuthBuild.ps1 -Action Check
./scripts/Local-OAuthBuild.ps1 -Action Publish -Version 0.8.78.1
```

The version is an example; choose the intended test version explicitly. It appears in the application caption. `-Runtime win-arm64` is also supported.

Credentials are stored outside Git, by default at `C:\Docs\QuickMail.BuildSecrets\google-oauth.dpapi`, encrypted with Windows DPAPI CurrentUser. A different machine/user must import again; retain the original JSON securely. Never paste credentials into chat, commit them, or include them in diagnostic logs. Import does not delete the original JSON. `-Replace` explicitly permits replacing an existing store.

Publish fails before compilation if credentials are unavailable. It injects a unique temporary C# source with a private ACL, removes it in finally, and leaves the existing CI credential source untouched. A killed PowerShell process can leave that private temporary file behind. The executable necessarily embeds the Desktop client identity, just like the official release; this is not a means of keeping a desktop client's secret undiscoverable. Do not enable compiler source embedding/binlogs containing credentials.

Output is a fresh `Artifacts/local-<version>-<runtime>-<unique id>` directory with EQM, Assets, BackupAgent and Updater. No existing installation, database, Google token, configuration, or GitHub release is changed. The importer/installer is not built. Use this script for local test packages; legacy build.bat/direct dotnet commands do not load this store automatically.

Validation checks credential structure, not ownership or Google's acceptance. Import the same client used for existing authorizations; a different/revoked client can require reauthorization. Official CI keeps using its existing injection mechanism.
