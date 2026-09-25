#Requires -Version 7.0
<#
.SYNOPSIS
    Enforces the ClashTray.Core coverage gate from a cobertura report.

.DESCRIPTION
    Locates the newest coverage.cobertura.xml under -CoverageDirectory, reads
    the per-package line/branch rates for -PackageName, and fails when either
    rate drops below its gate. Current baseline (v0.3.3-alpha.9, Core tests):
    76.4% lines, 69.0% branches. Gates sit one 5% notch below baseline; raise
    them as coverage improves instead of letting regressions through.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CoverageDirectory,

    [string]$PackageName = 'ClashTray.Core',

    [double]$MinimumLineRate = 70.0,

    [double]$MinimumBranchRate = 65.0
)

$ErrorActionPreference = 'Stop'

$file = Get-ChildItem -Path $CoverageDirectory -Recurse -Filter 'coverage.cobertura.xml' -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $file) {
    Write-Error "No coverage.cobertura.xml found under '$CoverageDirectory'. Run core tests with --collect:`"XPlat Code Coverage`" first."
    exit 1
}

[xml]$coverage = Get-Content -LiteralPath $file.FullName -Raw
$package = @($coverage.coverage.packages.package) | Where-Object { $_.name -eq $PackageName } | Select-Object -First 1
if ($null -eq $package) {
    $available = @($coverage.coverage.packages.package) | ForEach-Object { $_.name }
    Write-Error "Package '$PackageName' not found in '$($file.FullName)'. Available: $($available -join ', ')."
    exit 1
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$lineRate = [double]::Parse($package.'line-rate', $invariant) * 100
$branchRate = [double]::Parse($package.'branch-rate', $invariant) * 100
Write-Host ("{0}: line coverage {1:F2}% (gate {2:F0}%), branch coverage {3:F2}% (gate {4:F0}%)" -f $PackageName, $lineRate, $MinimumLineRate, $branchRate, $MinimumBranchRate)

$failures = @()
if ($lineRate -lt $MinimumLineRate) {
    $failures += ("line coverage {0:F2}% is below the {1:F0}% gate" -f $lineRate, $MinimumLineRate)
}
if ($branchRate -lt $MinimumBranchRate) {
    $failures += ("branch coverage {0:F2}% is below the {1:F0}% gate" -f $branchRate, $MinimumBranchRate)
}
if ($failures.Count -gt 0) {
    Write-Error ("Coverage gate failed for {0}: {1}" -f $PackageName, ($failures -join '; '))
    exit 1
}

Write-Host "Coverage gate passed."
