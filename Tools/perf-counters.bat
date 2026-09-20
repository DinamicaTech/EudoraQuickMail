@echo off
setlocal

:: Collect QuickMail perf counters to a timestamped CSV in the logs\ folder.
:: Refresh interval defaults to 5 seconds; pass a number to override: perf-counters 3

set INTERVAL=5
if not "%1"=="" set INTERVAL=%1

where dotnet-counters >nul 2>&1
if errorlevel 1 (
    echo dotnet-counters not found. Installing...
    dotnet tool install -g dotnet-counters
    if errorlevel 1 (
        echo Install failed. Check that dotnet is on your PATH.
        exit /b 1
    )
)

if not exist logs mkdir logs

:: Build a locale-safe timestamp string for the filename (yyyyMMdd-HHmmss)
for /f "delims=" %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set TS=%%i
set OUTFILE=logs\quickmail-perf-%TS%.csv

echo Waiting for QuickMail process...
:wait
tasklist /fi "imagename eq EudoraQM.exe" 2>nul | find /i "EudoraQM.exe" >nul
if errorlevel 1 (
    timeout /t 2 /nobreak >nul
    goto wait
)

echo Collecting to %OUTFILE% (refresh every %INTERVAL%s). Press Ctrl+C to stop.
dotnet-counters collect --name QuickMail --refresh-interval %INTERVAL% --counters System.Runtime --format csv --output %OUTFILE%

echo.
echo Saved to %OUTFILE%

endlocal
