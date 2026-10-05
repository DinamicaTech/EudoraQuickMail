[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Import', 'Check', 'Publish')][string]$Action,
    [string]$ClientJson,
    [string]$Store = 'C:\Docs\QuickMail.BuildSecrets\google-oauth.dpapi',
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$Replace
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Client($client) {
    if ($null -eq $client -or
        [string]$client.client_id -notmatch '^[0-9]+-[a-zA-Z0-9-]+\.apps\.googleusercontent\.com$' -or
        [string]$client.client_secret -notmatch '^[a-zA-Z0-9_-]{10,}$') {
        throw 'Missing or invalid Desktop OAuth credentials. Import the existing official Google client JSON.'
    }
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Store = [IO.Path]::GetFullPath($Store)
if ($Store.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The encrypted credential store must be outside the repository.'
}

if ($Action -eq 'Import') {
    if (-not $ClientJson) { throw 'Specify -ClientJson with the downloaded official Desktop client JSON path.' }
    if ((Test-Path -LiteralPath $Store) -and -not $Replace) {
        throw 'A credential store already exists. Use -Replace only when deliberately replacing it.'
    }
    try {
        $document = Get-Content -LiteralPath $ClientJson -Raw | ConvertFrom-Json
        $client = $document.installed
        Assert-Client $client
    } catch { throw 'Cannot read valid Desktop OAuth credentials from the supplied JSON. Values were not logged.' }
    $directory = Split-Path -Parent $Store
    [void][IO.Directory]::CreateDirectory($directory)
    # DPAPI CurrentUser: this encrypted file is usable only by this Windows identity.
    $payload = @{ client_id = $client.client_id; client_secret = $client.client_secret } | ConvertTo-Json -Compress
    $secure = ConvertTo-SecureString $payload -AsPlainText -Force
    try {
        $encrypted = ConvertFrom-SecureString $secure
        [IO.File]::WriteAllText($Store, $encrypted)
    } finally { $secure.Dispose(); $payload = $null; $client = $null; $document = $null }
    Write-Host 'Local OAuth credentials saved with Windows user encryption. No tokens or application settings were changed.'
    return
}

if (-not (Test-Path -LiteralPath $Store -PathType Leaf)) {
    throw 'No local OAuth credential store. First run this script with -Action Import -ClientJson <official JSON path>.'
}
try {
    $secure = ConvertTo-SecureString (Get-Content -LiteralPath $Store -Raw)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $client = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) | ConvertFrom-Json }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr); $secure.Dispose() }
    Assert-Client $client
} catch { throw 'Cannot decrypt or validate local OAuth credentials. Import them using this Windows account.' }
if ($Action -eq 'Check') {
    Write-Host 'Local OAuth credentials are available and structurally valid (no online authentication was performed).'
    return
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Publish requires an explicit numeric -Version (for example 0.8.78.1).' }
$parsedVersion = [version]$Version
if ($parsedVersion.Major -gt 65534 -or $parsedVersion.Minor -gt 65534 -or $parsedVersion.Build -gt 65534 -or $parsedVersion.Revision -gt 65534) {
    throw 'Each version component must be less than 65535.'
}
[void](Get-Command dotnet -ErrorAction Stop)
$runId = [guid]::NewGuid().ToString('N')
$output = Join-Path $repo "Artifacts\local-$Version-$Runtime-$runId"
$source = Join-Path (Split-Path -Parent $Store) "oauth-build-$runId.cs"
try {
    # Create the temporary file with a private ACL BEFORE putting plaintext into it.
    $stream = [IO.File]::Open($source, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $stream.Dispose()
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($sid)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $source -AclObject $acl
    # Validation above restricts both values to characters safe in C# string literals.
    $code = 'namespace QuickMail.Services; public partial class GoogleOAuthService { private const string ClientId = "' + $client.client_id + '"; private const string ClientSecret = "' + $client.client_secret + '"; }'
    [IO.File]::WriteAllText($source, $code)
    $client = $null; $code = $null
    & dotnet publish (Join-Path $repo 'QuickMail\QuickMail.csproj') -c Release -r $Runtime --self-contained true -o $output "-p:GoogleOAuthCredentialsFile=$source" "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" -p:DebugType=None -p:DebugSymbols=false --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Local publish failed. Any partial output is not a usable release.' }
    Write-Host "Local build $Version ready: $output\EudoraQM.exe"
    Write-Host 'Includes Assets, BackupAgent and Updater. No GitHub release was created.'
} finally {
    # Only our unique temporary source; never remove the store or any prior build.
    if (Test-Path -LiteralPath $source -PathType Leaf) { Remove-Item -LiteralPath $source -ErrorAction Stop }
}
