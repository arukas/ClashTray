# Resolves the Mini variant's prerequisite floor versions from their single sources:
# the .NET major version from the App project TargetFramework, and the Windows App
# SDK major.minor from the central Microsoft.WindowsAppSDK package version.
# Build-EXE.ps1 must pass both to Inno via /D instead of keeping local literals.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$appProject = Join-Path $repoRoot 'src\ClashTray.App\ClashTray.App.csproj'
# A single -getProperty query returns the raw value, not a JSON envelope.
$targetFramework = [string](& $dotnet msbuild $appProject -getProperty:TargetFramework)
$targetFramework = $targetFramework.Trim()
if ($targetFramework -notmatch '^net(?<major>\d+)\.0(?:-|$)') {
    throw "App project TargetFramework '$targetFramework' does not look like net<major>.0[-...]; cannot derive the Mini .NET prerequisite."
}
$dotNetMajorVersion = $Matches.major

$centralPackagesPath = Join-Path $repoRoot 'Directory.Packages.props'
$centralPackages = [xml](Get-Content -LiteralPath $centralPackagesPath -Raw)
$windowsAppSdkVersion = [string] @($centralPackages.Project.ItemGroup.PackageVersion |
    Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' } |
    Select-Object -First 1)[0].Version
if ($windowsAppSdkVersion -notmatch '^(?<majorminor>\d+\.\d+)\.\d+$') {
    throw "Microsoft.WindowsAppSDK version '$windowsAppSdkVersion' in Directory.Packages.props does not look like <major>.<minor>.<patch>; cannot derive the Mini Windows App Runtime prerequisite."
}
$winAppRuntimeMajorMinor = $Matches.majorminor

[pscustomobject]@{
    DotNetMajorVersion       = $dotNetMajorVersion
    WinAppRuntimeMajorMinor  = $winAppRuntimeMajorMinor
}
