param([string]$Updater = "$PSScriptRoot\..\Artifacts\UpdaterValidation\QuickMail.Updater.exe")
$ErrorActionPreference = 'Stop'
$Updater = (Resolve-Path -LiteralPath $Updater).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('EQM-Updater-Smoke-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $testRoot 'application'
$work = Join-Path $testRoot 'work'
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Path $target,$work,$stage,(Join-Path $target 'Data'),(Join-Path $target 'Assets'),(Join-Path $stage 'Assets') | Out-Null
# Harmless OS command process stands in for EQM: no real profile or application is started.
$dummy = Join-Path $target 'EudoraQM.exe'
Copy-Item -LiteralPath "$env:WINDIR\System32\cmd.exe" -Destination $dummy
Copy-Item -LiteralPath $dummy -Destination (Join-Path $stage 'EudoraQM.exe')
Copy-Item -LiteralPath $Updater -Destination (Join-Path $work 'QuickMail.Updater.exe')
Set-Content -LiteralPath (Join-Path $target 'Data\mail.db') -Value 'synthetic-mail-do-not-change'
Set-Content -LiteralPath (Join-Path $target 'Assets\version.txt') -Value 'old'
Set-Content -LiteralPath (Join-Path $stage 'Assets\version.txt') -Value 'new'
$dummyProcess = Start-Process -FilePath $dummy -ArgumentList '/c ping -n 8 127.0.0.1 >nul' -WindowStyle Hidden -PassThru
$restarted = Join-Path $target 'restarted.txt'
$request = @{
    Target = $target; Pid = $dummyProcess.Id; Started = $dummyProcess.StartTime.ToUniversalTime().Ticks
    WorkingDirectory = $target; Arguments = @('/c', 'echo', 'restarted', '>', 'restarted.txt')
}
$requestPath = Join-Path $work 'request.json'
$request | ConvertTo-Json | Set-Content -LiteralPath $requestPath
$helperProcess = Start-Process -FilePath (Join-Path $work 'QuickMail.Updater.exe') -ArgumentList "`"$requestPath`"" -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (-not (Test-Path -LiteralPath (Join-Path $work 'ready'))) {
    if ([DateTime]::UtcNow -gt $deadline) { throw "Updater did not become ready; inspect $work" }
    Start-Sleep -Milliseconds 100
}
Set-Content -LiteralPath (Join-Path $work 'commit') -Value 'test-approved'
while (-not (Test-Path -LiteralPath $restarted)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw "Updater did not restart the dummy application; inspect $work" }
    Start-Sleep -Milliseconds 100
}
if ((Get-Content -LiteralPath (Join-Path $target 'Assets\version.txt')).Trim() -ne 'new') { throw 'New asset missing' }
if ((Get-Content -LiteralPath (Join-Path $work 'previous\Assets\version.txt')).Trim() -ne 'old') { throw 'Previous asset missing' }
if ((Get-Content -LiteralPath (Join-Path $target 'Data\mail.db')).Trim() -ne 'synthetic-mail-do-not-change') { throw 'Data changed' }
Write-Output "PASS: detached helper, normal exit wait, replacement, previous files, preserved Data and restart. Fixtures: $testRoot"
