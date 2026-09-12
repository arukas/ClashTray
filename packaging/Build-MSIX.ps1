[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $CertificatePath,
    [string] $PackageVersion = '0.2.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stageRoot = Join-Path $PSScriptRoot '.stage'
$outputRoot = Join-Path $PSScriptRoot 'out'
$appProject = Join-Path $repoRoot 'src\ClashTray.App\ClashTray.App.csproj'
$serviceProject = Join-Path $repoRoot 'src\ClashTray.Service\ClashTray.Service.csproj'
$appPublish = Join-Path $stageRoot 'app-publish'
$servicePublish = Join-Path $stageRoot 'service-publish'
$packageRoot = Join-Path $stageRoot 'package'
$msixPath = Join-Path $outputRoot "ClashTray-$Configuration.msix"
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$makeAppx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'

if (-not (Test-Path -LiteralPath $dotnet)) { throw "Missing .NET SDK: $dotnet" }
if (-not (Test-Path -LiteralPath $makeAppx)) { throw "Missing MakeAppx: $makeAppx" }
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "PackageVersion must use four numeric components, for example 0.2.0.0." }

Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $appPublish, $servicePublish, $packageRoot, $outputRoot -Force | Out-Null

& $dotnet publish $appProject --configuration $Configuration --runtime win-x64 --self-contained true --output $appPublish --property:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "ClashTray.App publish failed with exit code $LASTEXITCODE." }
& $dotnet publish $serviceProject --configuration $Configuration --runtime win-x64 --self-contained true --output $servicePublish --no-restore
if ($LASTEXITCODE -ne 0) { throw "ClashTray.Service publish failed with exit code $LASTEXITCODE." }

Copy-Item -Path (Join-Path $appPublish '*') -Destination $packageRoot -Recurse -Force
Copy-Item -Path (Join-Path $servicePublish '*') -Destination $packageRoot -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Assets') -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'ClashTray.Package\Assets\*') -Destination (Join-Path $packageRoot 'Assets') -Recurse -Force

$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'ClashTray.Package\Package.appxmanifest')
$manifest = $manifest.Replace('$targetnametoken$.ClashTray.App.exe', 'ClashTray.App.exe').Replace('$targetentrypoint$', 'Windows.FullTrustApplication')
$manifest = $manifest -replace '(<Identity\b[^>]*\bVersion=")[^"]+(\")', ('${1}' + $PackageVersion + '${2}')
Set-Content -LiteralPath (Join-Path $packageRoot 'AppxManifest.xml') -Value $manifest -Encoding utf8

Remove-Item -LiteralPath $msixPath -Force -ErrorAction SilentlyContinue
& $makeAppx pack /d $packageRoot /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }

if ($CertificatePath) {
    if (-not (Test-Path -LiteralPath $signtool)) { throw "Missing SignTool: $signtool" }
    & $signtool sign /fd SHA256 /a /f $CertificatePath $msixPath
    if ($LASTEXITCODE -ne 0) { throw "MSIX signing failed with exit code $LASTEXITCODE." }
}

Write-Output "Created $msixPath"
