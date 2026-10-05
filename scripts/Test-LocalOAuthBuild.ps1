$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('eqm-oauth-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$json = Join-Path $root 'fake.json'
$store = Join-Path $root 'fake.dpapi'
$source = Join-Path $root 'fake.cs'
$script = Join-Path $PSScriptRoot 'Local-OAuthBuild.ps1'
function Expect-Failure([scriptblock]$operation) {
    $failed = $false
    try { & $operation } catch { $failed = $true }
    if (-not $failed) { throw 'Expected operation to fail.' }
}
try {
    Expect-Failure { & $script -Action Check -Store $store }
    [IO.File]::WriteAllText($json, '{"installed":{"client_id":"123456-test.apps.googleusercontent.com","client_secret":"FAKE_TEST_ONLY_123456"}}')
    & $script -Action Import -ClientJson $json -Store $store
    if ([IO.File]::ReadAllText($store).Contains('FAKE_TEST')) { throw 'Store contains plaintext.' }
    & $script -Action Check -Store $store
    Expect-Failure { & $script -Action Import -ClientJson $json -Store $store }
    Expect-Failure { & $script -Action Publish -Store $store -Version invalid }
    [IO.File]::WriteAllText($json, '{"web":{"client_id":"123456-test.apps.googleusercontent.com","client_secret":"FAKE_TEST_ONLY_123456"}}')
    Expect-Failure { & $script -Action Import -ClientJson $json -Store $store -Replace }
    & $script -Action Check -Store $store
    [IO.File]::WriteAllText($source, 'namespace QuickMail.Services; public partial class GoogleOAuthService { private const string ClientId = "123456-test.apps.googleusercontent.com"; private const string ClientSecret = "FAKE_TEST_ONLY_123456"; }')
    $project = Join-Path $PSScriptRoot '..\QuickMail\QuickMail.csproj'
    & dotnet build $project -c Debug "-p:GoogleOAuthCredentialsFile=$source" --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'External credential source compilation failed.' }
    Write-Host 'PASS: missing store, encrypted roundtrip, overwrite protection, invalid version, web-client rejection and external source compilation.'
} finally {
    foreach ($file in @($json, $store, $source)) {
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -ErrorAction Stop }
    }
    # Nonrecursive: fails safely if anything unexpected exists.
    [IO.Directory]::Delete($root, $false)
}
