[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repositoryRoot ('artifacts/security-capabilities-' + [guid]::NewGuid().ToString('N'))
}
if (Test-Path -LiteralPath (Join-Path $ResultsDirectory 'security.trx')) { throw 'Security gate results must be fresh.' }
Push-Location $repositoryRoot
try {
    dotnet test tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj -c $Configuration -p:Platform=x64 --no-build --filter 'TestCategory=RequiresWindowsAcl|TestCategory=RequiresRestrictedToken|TestCategory=RequiresTls' --logger 'trx;LogFileName=security.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw "Required Windows security tests failed with exit code $LASTEXITCODE. Missing environment capabilities are not passes." }
}
finally { Pop-Location }
$trxPath = Join-Path $ResultsDirectory 'security.trx'
$trx = [Xml.XmlDocument]::new()
$trx.Load($trxPath)
$results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
$expected = 0
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests/ClashTray.IntegrationTests') -Filter '*.cs' -File | ForEach-Object {
    $expected += [regex]::Matches([IO.File]::ReadAllText($_.FullName), '\[TestCategory\("Requires(?:WindowsAcl|RestrictedToken|Tls)"\)\]').Count
}
if ($expected -eq 0 -or $results.Count -ne $expected -or @($results | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' }).Count -ne 0) {
    throw "Security gate must execute and pass all $expected declared capability tests; found $($results.Count). Skips are failures."
}
Write-Host "Windows security capability gate passed: $expected tests; $trxPath"
