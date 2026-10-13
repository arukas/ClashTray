[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repositoryRoot ('artifacts/release-tests-' + [guid]::NewGuid().ToString('N'))
}
if (Test-Path -LiteralPath $ResultsDirectory) {
    throw 'Release suite result directory must be fresh.'
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

function Invoke-RequiredTestProject {
    param([string]$Project, [string]$ResultName)

    # Match the CI order and execute each project separately. This keeps the
    # complete suite without overlapping process/port fixtures across projects.
    $arguments = @(
        'test', (Join-Path $repositoryRoot $Project),
        '--configuration', $Configuration,
        '--property:Platform=x64', '--no-build', '--no-restore',
        '--logger', "trx;LogFileName=$ResultName.trx",
        '--results-directory', $ResultsDirectory
    )
    & dotnet @arguments
    $testExitCode = $LASTEXITCODE
    $trxPath = Join-Path $ResultsDirectory "$ResultName.trx"
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Required release suite produced no TRX: $Project (exit $testExitCode)."
    }
    $trx = [Xml.XmlDocument]::new()
    $trx.Load($trxPath)
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($results.Count -eq 0) {
        throw "Required release suite ran no tests: $Project."
    }
    $nonPassing = @($results | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' })
    foreach ($result in $nonPassing) {
        $messageNode = $result.SelectSingleNode(".//*[local-name()='ErrorInfo']/*[local-name()='Message']")
        $detail = "$($result.GetAttribute('testName')): $($result.GetAttribute('outcome'))"
        if ($null -ne $messageNode) { $detail += ': ' + $messageNode.InnerText }
        if ($detail.Length -gt 1600) { $detail = $detail.Substring(0, 1600) }
        $escaped = $detail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error title=Required release test failed::$escaped"
    }
    if ($testExitCode -ne 0 -or $nonPassing.Count -ne 0) {
        throw "Required release suite failed: $Project; exit $testExitCode, $($nonPassing.Count) non-passing tests. Skips are failures."
    }
    Write-Host "Required release suite passed: $($results.Count) tests; $trxPath"
}

Push-Location $repositoryRoot
try {
    Invoke-RequiredTestProject -Project 'tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj' -ResultName 'integration'
    Invoke-RequiredTestProject -Project 'tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj' -ResultName 'core'
}
finally { Pop-Location }
