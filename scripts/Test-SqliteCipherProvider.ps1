[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts/test-results/sqlite-cipher-' + [Guid]::NewGuid().ToString('N'))
}
$project = Join-Path $repoRoot 'tests/TeleSelfCloud.SqliteCipherProbe/TeleSelfCloud.SqliteCipherProbe.csproj'
& dotnet build $project -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Cipher probe build failed.' }
$binary = Join-Path $repoRoot 'tests/TeleSelfCloud.SqliteCipherProbe/bin/Release/net10.0/TeleSelfCloud.SqliteCipherProbe.dll'
& dotnet $binary ([IO.Path]::GetFullPath($OutputDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Cipher probe failed. Evidence was kept.' }
$report = Get-Content -LiteralPath (Join-Path $OutputDirectory 'probe-report.json') -Raw | ConvertFrom-Json
if (!$report.AllPassed -or $report.Version -ne 1 -or $report.Cipher -ne 'chacha20' -or $report.CipherLibrary -ne 'SQLite3 Multiple Ciphers 2.4.0') { throw 'Cipher checks are incomplete.' }
foreach ($required in @('EncryptedDatabaseHasNoPlainName','EncryptedWalHasNoPlainName','Fts5Works','IntegrityCheck','NoPlainSQLiteHeader','CorrectKeyReopen','MissingKeyRejected','WrongKeyRejected','PageTamperingRejected','TruncationRejected','PlainDatabaseCompatibility','PlainCopyRekeyPreservesRows')) {
    if ($report.Checks.$required -isnot [bool] -or !$report.Checks.$required) { throw "Missing/failed cipher check: $required" }
}
