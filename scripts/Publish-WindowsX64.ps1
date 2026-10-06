[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('..\artifacts\publish\win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'src\TeleSelfCloud.Desktop\TeleSelfCloud.Desktop.csproj'
$solutionPath = Join-Path $repoRoot 'TeleSelfCloud.slnx'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))

if ($outputPath.TrimEnd('\') -eq $repoRoot.TrimEnd('\')) {
    throw 'The publish output cannot be the repository root.'
}
if ($outputPath.StartsWith($repoRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase) -and
    -not $outputPath.StartsWith($artifactRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Outputs within the workspace must stay under $artifactRoot."
}
if (Test-Path -LiteralPath $outputPath) {
    $existing = Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1
    if ($existing) { throw "Publish output already contains files: $outputPath. Choose a new output directory; the script will not delete existing data." }
}

Write-Host 'Running the Release unit suite...'
& (Join-Path $PSScriptRoot 'Test-SqliteCipherProvider.ps1')
if ($LASTEXITCODE -ne 0) { throw 'The mandatory native SQLite cipher probe failed.' }
$testProjectPath = Join-Path $repoRoot 'tests\TeleSelfCloud.Tests\TeleSelfCloud.Tests.csproj'
$testResultsPath = Join-Path $artifactRoot ('test-results\publish-' + [Guid]::NewGuid().ToString('N'))
& dotnet test $testProjectPath -c Release -warnaserror --logger 'trx;LogFileName=release.trx' --results-directory $testResultsPath
if ($LASTEXITCODE -ne 0) { throw "Release tests failed with exit code $LASTEXITCODE." }
$testResultFile = Join-Path $testResultsPath 'release.trx'
if (-not (Test-Path -LiteralPath $testResultFile -PathType Leaf)) {
    throw 'Release tests produced no result file. Publishing requires real, discovered tests.'
}
[xml]$testResults = Get-Content -LiteralPath $testResultFile -Raw
$testCounters = $testResults.SelectSingleNode("//*[local-name()='Counters']")
$testSummary = $testResults.SelectSingleNode("//*[local-name()='ResultSummary']")
if ($null -eq $testSummary -or $testSummary.outcome -ne 'Completed' -or $null -eq $testCounters -or [int]$testCounters.total -le 0 -or
    [int]$testCounters.executed -ne [int]$testCounters.total -or
    [int]$testCounters.passed -ne [int]$testCounters.total) {
    throw 'Release tests were empty, skipped, or not all passed. Publishing was stopped.'
}

& dotnet build $solutionPath -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

Write-Host 'Publishing self-contained win-x64...'
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$requiredFiles = @(
    'TeleSelfCloud.Desktop.exe',
    'TeleSelfCloud.Desktop.deps.json',
    'TeleSelfCloud.Desktop.runtimeconfig.json',
    'tdjson.dll',
    'z.dll',
    'libcrypto-3-x64.dll',
    'libssl-3-x64.dll',
    'sqlite3mc.dll',
    'THIRD-PARTY-NOTICES.md',
    'INSTALL_WINDOWS.md',
    'licenses\Apache-2.0.txt',
    'licenses\BSL-1.0.txt',
    'licenses\DOTNET-THIRD-PARTY-NOTICES.txt',
    'licenses\MIT.txt',
    'licenses\SQLITE3MC-MIT.txt',
    'licenses\WINDOWS-DESKTOP-RUNTIME-LICENSE.txt',
    'licenses\Zlib.txt'
)

$missingFiles = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputPath $_) -PathType Leaf) })
if ($missingFiles.Count -gt 0) {
    throw "Publish is missing required files: $($missingFiles -join ', ')"
}

$deps = Get-Content -LiteralPath (Join-Path $outputPath 'TeleSelfCloud.Desktop.deps.json') -Raw | ConvertFrom-Json
$runtimeConfig = Get-Content -LiteralPath (Join-Path $outputPath 'TeleSelfCloud.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
$libraries = @($deps.libraries.PSObject.Properties.Name)
if ($libraries | Where-Object { $_ -like 'SQLitePCLRaw.bundle_e_sqlite3/*' -or $_ -like 'Microsoft.Data.Sqlite/*' }) {
    throw 'An incompatible default SQLite bundle is still included.'
}
if (Test-Path -LiteralPath (Join-Path $outputPath 'e_sqlite3.dll')) { throw 'An obsolete SQLite native library is still included.' }
$getLibraryVersion = {
    param([string]$prefix)
    $library = $libraries | Where-Object { $_.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($library -and $library.Contains('/')) { return $library.Substring($library.IndexOf('/') + 1) }
    return $null
}
$runtimeVersions = @{}
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $runtimeVersions[$framework.name] = $framework.version
}

$manifestPath = Join-Path $outputPath 'teleSelfCloud-build-manifest.json'
$checksumPath = Join-Path $outputPath 'teleSelfCloud-build-manifest.sha256'
$excludedPaths = @(
    [System.IO.Path]::GetFullPath($manifestPath),
    [System.IO.Path]::GetFullPath($checksumPath)
)
$packageFiles = @(Get-ChildItem -LiteralPath $outputPath -File -Recurse | Where-Object {
    [System.Array]::IndexOf($excludedPaths, [System.IO.Path]::GetFullPath($_.FullName)) -lt 0
})
$outputPrefix = $outputPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

$fileEntries = foreach ($file in $packageFiles) {
    $fullPath = [System.IO.Path]::GetFullPath($file.FullName)
    if (-not $fullPath.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Published file escaped the output directory: $fullPath"
    }
    $relativePath = $fullPath.Substring($outputPrefix.Length).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
    [pscustomobject]@{
        path = $relativePath
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifest = [pscustomobject]@{
    schemaVersion = 1
    product = 'TeleSelfCloud Desktop'
    configuration = 'Release'
    targetFramework = $runtimeConfig.runtimeOptions.tfm
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    components = [pscustomobject]@{
        dotnet = $runtimeVersions
        tdlib = (& $getLibraryVersion 'tdlib.native.win-x64/')
        microsoftDataSqlite = (& $getLibraryVersion 'Microsoft.Data.Sqlite.Core/')
        sqlitePclRaw = (& $getLibraryVersion 'SQLitePCLRaw.core/')
        sqlite3MultipleCiphers = (& $getLibraryVersion 'SQLite3MC.PCLRaw.bundle/')
    }
    prerequisite = 'Microsoft Visual C++ Redistributable 2015-2022 (x64), required by the TDLib native package.'
    package = [pscustomobject]@{
        fileCount = $packageFiles.Count
        totalBytes = [long](($packageFiles | Measure-Object -Property Length -Sum).Sum)
    }
    files = @($fileEntries)
    checksumExcludes = @('teleSelfCloud-build-manifest.json', 'teleSelfCloud-build-manifest.sha256')
}

if ([string]::IsNullOrWhiteSpace($manifest.components.microsoftDataSqlite)) {
    throw 'The release manifest is missing the managed SQLite provider version.'
}
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($checksumPath, "$manifestHash  teleSelfCloud-build-manifest.json`n", [System.Text.UTF8Encoding]::new($false))

Write-Host "Published $($packageFiles.Count) files ($($manifest.package.totalBytes) bytes) to $outputPath"
Write-Host "Build manifest: $manifestPath"
Write-Host "Manifest SHA-256: $manifestHash"
