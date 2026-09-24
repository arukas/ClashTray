[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ExistingCorePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot 'mihomo-release.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if (
    $manifest.version -notmatch '^v\d+\.\d+\.\d+$' -or
    $manifest.windowsAmd64Sha256 -notmatch '^[a-fA-F0-9]{64}$'
) {
    throw 'The pinned Mihomo version or Windows x64 archive SHA-256 is invalid.'
}

$corePath = $null
if (-not [string]::IsNullOrWhiteSpace($ExistingCorePath)) {
    if (-not (Test-Path -LiteralPath $ExistingCorePath -PathType Leaf)) {
        throw "The explicitly supplied Mihomo executable does not exist: $ExistingCorePath"
    }

    $corePath = (Resolve-Path -LiteralPath $ExistingCorePath).Path
    Write-Host 'Using an existing controlled Mihomo executable; archive download and SHA-256 verification were not performed by this invocation.'
}
else {
    $temporaryRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        $env:RUNNER_TEMP
    }
    else {
        [IO.Path]::GetTempPath()
    }

    $archiveName = "mihomo-windows-amd64-$($manifest.version).zip"
    $archiveUri = "https://github.com/MetaCubeX/mihomo/releases/download/$($manifest.version)/$archiveName"
    $archivePath = Join-Path $temporaryRoot $archiveName
    $extractRoot = Join-Path $temporaryRoot "clashtray-mihomo-test-$($manifest.version)-$([guid]::NewGuid().ToString('N'))"

    Invoke-WebRequest -Uri $archiveUri -OutFile $archivePath
    $actualArchiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualArchiveSha256 -ine $manifest.windowsAmd64Sha256) {
        throw "Official Mihomo archive SHA-256 mismatch. Expected $($manifest.windowsAmd64Sha256), received $actualArchiveSha256."
    }

    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    $executables = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter 'mihomo-windows-amd64.exe')
    if ($executables.Count -ne 1 -or $executables[0].Length -eq 0) {
        throw "The verified archive must contain exactly one non-empty mihomo-windows-amd64.exe; found $($executables.Count)."
    }

    $corePath = $executables[0].FullName
    Write-Host "Verified official Mihomo archive SHA-256: $actualArchiveSha256"
}

$stream = [IO.File]::OpenRead($corePath)
$reader = $null
try {
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    $stream.Position = 0x3C
    $peOffset = $reader.ReadInt32()
    if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
        throw 'The Mihomo executable has an invalid PE header offset.'
    }

    $stream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550) {
        throw 'The Mihomo executable does not contain a valid PE signature.'
    }

    $machine = $reader.ReadUInt16()
    if ($machine -ne 0x8664) {
        throw ('The Mihomo executable is not Windows x64 (PE machine 0x{0:X4}).' -f $machine)
    }
}
finally {
    if ($null -ne $reader) {
        $reader.Dispose()
    }

    $stream.Dispose()
}

$versionOutput = (& $corePath -v 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch [regex]::Escape($manifest.version)) {
    throw "The executable does not report the pinned Mihomo version $($manifest.version). Output: $versionOutput"
}

$env:CLASHTRAY_MIHOMO_PATH = $corePath
$env:CLASHTRAY_MIHOMO_REQUIRED = 'true'
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
    $environmentLines = "CLASHTRAY_MIHOMO_PATH=$corePath" + [Environment]::NewLine
    $environmentLines += "CLASHTRAY_MIHOMO_REQUIRED=true" + [Environment]::NewLine
    [IO.File]::AppendAllText(
        $env:GITHUB_ENV,
        $environmentLines,
        [Text.UTF8Encoding]::new($false))
}

$temporaryRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    $env:RUNNER_TEMP
}
else {
    [IO.Path]::GetTempPath()
}
$resultsDirectory = Join-Path $temporaryRoot 'official-mihomo-results'
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$testProject = Join-Path $repositoryRoot 'tests\ClashTray.IntegrationTests\ClashTray.IntegrationTests.csproj'
$testArguments = @(
    'test',
    $testProject,
    '--configuration', $Configuration,
    '--no-restore',
    '--no-build',
    '--filter', 'TestCategory=RequiresOfficialMihomo',
    '--logger', 'trx;LogFileName=official-mihomo.trx',
    '--results-directory', $resultsDirectory
)

Push-Location $repositoryRoot
try {
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The mandatory official Mihomo integration gate failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$trxPath = Join-Path $resultsDirectory 'official-mihomo.trx'
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw 'The mandatory official Mihomo integration gate did not produce its TRX result file.'
}

$trx = [Xml.XmlDocument]::new()
$trx.Load($trxPath)
$testResults = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
if ($testResults.Count -eq 0) {
    throw 'The mandatory official Mihomo category did not execute any tests.'
}

$expectedTestCount = 0
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests\ClashTray.IntegrationTests') -Filter '*.cs' -File | ForEach-Object {
    $sourceText = [IO.File]::ReadAllText($_.FullName)
    $expectedTestCount += [regex]::Matches(
        $sourceText,
        '\[TestCategory\("RequiresOfficialMihomo"\)\]').Count
}
if ($testResults.Count -ne $expectedTestCount) {
    throw "The official Mihomo category ran $($testResults.Count) tests, but $expectedTestCount are declared. A test was skipped or not discovered."
}

$nonPassing = @($testResults | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' })
if ($nonPassing.Count -gt 0) {
    $outcomes = ($nonPassing | ForEach-Object { $_.GetAttribute('outcome') } | Sort-Object -Unique) -join ', '
    throw "The mandatory official Mihomo category must pass every selected test; non-passing outcomes: $outcomes."
}

Write-Host "Official Mihomo gate passed: $($testResults.Count) category tests; version $($manifest.version); executable $corePath"
Write-Host "Test result: $trxPath"
