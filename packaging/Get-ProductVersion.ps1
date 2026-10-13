# Resolves the single-source product version from MSBuild (Directory.Build.props).
# Every packaging script and release check must read the version through this
# helper instead of keeping a local hardcoded copy.
[CmdletBinding()]
param(
    [string] $ProjectPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot 'src\ClashTray.App\ClashTray.App.csproj'
}

if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    throw "Version probe project was not found: $ProjectPath"
}

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$evaluation = (& $dotnet msbuild $ProjectPath -getProperty:Version -getProperty:VersionPrefix) | ConvertFrom-Json
$version = [string] $evaluation.Properties.Version
$versionPrefix = [string] $evaluation.Properties.VersionPrefix

if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "MSBuild Version '$version' does not look like 1.2.3 or 1.2.3-alpha.1. Fix VersionPrefix/VersionSuffix in Directory.Build.props."
}

[pscustomobject]@{
    Version       = $version
    VersionPrefix = $versionPrefix
}
