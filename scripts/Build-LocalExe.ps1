[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Store = 'C:\Docs\QuickMail.BuildSecrets\google-oauth.dpapi',
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publisher = Join-Path $PSScriptRoot 'Local-OAuthBuild.ps1'
$project = Join-Path $repo 'QuickMail\QuickMail.csproj'
$artifacts = Join-Path $repo 'Artifacts'
if (-not (Test-Path -LiteralPath $publisher -PathType Leaf)) {
    throw "Publisher not found: $publisher"
}

if (-not $Version) {
    $projectVersionText = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
    if ($projectVersionText -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw 'Cannot determine the project version. Specify -Version explicitly.'
    }
    $defaultVersion = $projectVersionText
    if (Test-Path -LiteralPath $artifacts -PathType Container) {
        $pattern = '^local-(\d+\.\d+\.\d+(?:\.\d+)?)-' + [regex]::Escape($Runtime) + '-[0-9a-f]{32}$'
        $latestBuild = Get-ChildItem -LiteralPath $artifacts -Directory -Filter "local-*-$Runtime-*" |
            Where-Object {
                $_.Name -match $pattern -and
                (Test-Path -LiteralPath (Join-Path $_.FullName 'EudoraQM.exe') -PathType Leaf)
            } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $latestBuild) {
            [void]($latestBuild.Name -match $pattern)
            $defaultVersion = $Matches[1]
        }
    }
    $answer = Read-Host "Version [$defaultVersion]"
    $Version = if ([string]::IsNullOrWhiteSpace($answer)) { $defaultVersion } else { $answer.Trim() }
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw 'Version must have three or four numeric components, for example 0.8.86.11.'
}

# Check without printing the OAuth client identity or its secret. The existing publisher
# validates the version, injects a private temporary source and builds every component.
& $publisher -Action Check -Store $Store
if ($CheckOnly) {
    Write-Host "Ready to build Eudora QuickMail $Version ($Runtime)."
    return
}

Write-Host "Building Eudora QuickMail $Version ($Runtime)..."
& $publisher -Action Publish -Store $Store -Runtime $Runtime -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$result = Get-ChildItem -LiteralPath $artifacts -Directory -Filter "local-$Version-$Runtime-*" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $result) { throw 'Publish returned without an output directory.' }
$exe = Join-Path $result.FullName 'EudoraQM.exe'
$agent = Join-Path $result.FullName 'BackupAgent\DinamicaBackupAgent.exe'
$updater = Join-Path $result.FullName 'Updater\QuickMail.Updater.exe'
foreach ($path in @($exe, $agent, $updater)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Publish output is incomplete: $path"
    }
}
Write-Host ''
Write-Host "EXE ready: $exe"
Write-Host "Copy the entire output folder when installing locally: $($result.FullName)"
