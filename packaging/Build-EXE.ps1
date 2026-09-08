[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$PackageVersion = '0.1.0',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $PSScriptRoot 'out'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}

$stageRoot = Join-Path $PSScriptRoot '.stage'
$payloadRoot = Join-Path $stageRoot 'payload'
$appPayload = Join-Path $payloadRoot 'App'
$servicePayload = Join-Path $payloadRoot 'Service'
$corePayload = Join-Path $payloadRoot 'Core'
$appPublish = Join-Path $stageRoot 'app'
$servicePublish = Join-Path $stageRoot 'service'
$setupPublish = Join-Path $stageRoot 'setup'
$appBuildOutput = Join-Path $repoRoot "src\ClashTray.App\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64"
$payloadZip = Join-Path $stageRoot 'ClashTray-Payload.zip'
$coreArchive = Join-Path $stageRoot 'mihomo-windows-amd64-v1.19.30.zip'
$coreExtract = Join-Path $stageRoot 'mihomo-extract'

$mihomoVersion = 'v1.19.30'
$mihomoArchiveUri = 'https://github.com/MetaCubeX/mihomo/releases/download/v1.19.30/mihomo-windows-amd64-v1.19.30.zip'
$mihomoArchiveSha256 = '22c09fd67673895ef7cd6b1820563918275c3d316f2462b306208675118db3c0'
$mihomoLicenseUri = 'https://raw.githubusercontent.com/MetaCubeX/mihomo/v1.19.30/LICENSE'

$appProject = Join-Path $repoRoot 'src\ClashTray.App\ClashTray.App.csproj'
$serviceProject = Join-Path $repoRoot 'src\ClashTray.Service\ClashTray.Service.csproj'
$setupProject = Join-Path $repoRoot 'src\ClashTray.Setup\ClashTray.Setup.csproj'

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Copy-PublishTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $items = Get-ChildItem -LiteralPath $Source -Force
    foreach ($item in $items) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Copy-AppXamlResources {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "The app build output directory was not found: $Source"
    }

    $resources = @(Get-ChildItem -LiteralPath $Source -File |
        Where-Object { $_.Extension -in '.xbf', '.pri' })
    if ((-not ($resources.Name -contains 'App.xbf')) -or (-not ($resources.Name -contains 'ClashTray.App.pri'))) {
        throw 'The app build output is missing compiled WinUI XAML resources (App.xbf or ClashTray.App.pri).'
    }

    foreach ($resource in $resources) {
        Copy-Item -LiteralPath $resource.FullName -Destination (Join-Path $Destination $resource.Name) -Force
    }
}

function Assert-WindowsAmd64Pe {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if (($bytes.Length -lt 0x40) -or ($bytes[0] -ne [byte][char]'M') -or ($bytes[1] -ne [byte][char]'Z')) {
        throw "Mihomo core is not a Windows executable: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if (($peOffset -lt 0) -or ($peOffset + 6 -gt $bytes.Length)) {
        throw "Mihomo core has an invalid PE header: $Path"
    }

    if (($bytes[$peOffset] -ne [byte][char]'P') -or ($bytes[$peOffset + 1] -ne [byte][char]'E') -or ($bytes[$peOffset + 2] -ne 0) -or ($bytes[$peOffset + 3] -ne 0) -or ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664)) {
        throw "Mihomo core is not a Windows x64 executable: $Path"
    }
}

function Prepare-MihomoPayload {
    New-Item -ItemType Directory -Path $corePayload, $coreExtract -Force | Out-Null
    Write-Host "Downloading official Mihomo $mihomoVersion core..."
    Invoke-WebRequest -Uri $mihomoArchiveUri -OutFile $coreArchive

    $actualHash = (Get-FileHash -LiteralPath $coreArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $mihomoArchiveSha256) {
        throw "Mihomo archive SHA-256 mismatch. Expected $mihomoArchiveSha256, received $actualHash."
    }

    Expand-Archive -LiteralPath $coreArchive -DestinationPath $coreExtract -Force
    $coreExecutables = @(Get-ChildItem -LiteralPath $coreExtract -Recurse -File |
        Where-Object { $_.Name -eq 'mihomo-windows-amd64.exe' })
    if ($coreExecutables.Count -ne 1) {
        throw "The official Mihomo archive did not contain exactly one expected Windows x64 executable."
    }

    Assert-WindowsAmd64Pe -Path $coreExecutables[0].FullName
    Copy-Item -LiteralPath $coreExecutables[0].FullName -Destination (Join-Path $corePayload 'mihomo.exe') -Force

    Invoke-WebRequest -Uri $mihomoLicenseUri -OutFile (Join-Path $corePayload 'Mihomo-LICENSE.txt')
    if (-not (Test-Path -LiteralPath (Join-Path $corePayload 'Mihomo-LICENSE.txt') -PathType Leaf)) {
        throw 'The Mihomo license file was not downloaded.'
    }

    @(
        "Mihomo version: $mihomoVersion"
        "Source archive: $mihomoArchiveUri"
        "Archive SHA-256: $mihomoArchiveSha256"
        'Architecture: Windows x64 (amd64)'
    ) | Set-Content -LiteralPath (Join-Path $corePayload 'Mihomo-Release.txt') -Encoding ascii
}

if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    throw "App project was not found: $appProject"
}
if (-not (Test-Path -LiteralPath $serviceProject -PathType Leaf)) {
    throw "Service project was not found: $serviceProject"
}
if (-not (Test-Path -LiteralPath $setupProject -PathType Leaf)) {
    throw "Setup project was not found: $setupProject"
}

Write-Host "Publishing ClashTray EXE installer ($Configuration, win-x64, version $PackageVersion)..."

# This is a generated staging directory owned by this script.
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot, $outputRoot -Force | Out-Null

Invoke-Dotnet @(
    'publish', $appProject,
    '--configuration', $Configuration,
    '--framework', 'net10.0-windows10.0.19041.0',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $appPublish,
    '--property:Platform=x64',
    '--property:WindowsPackageType=None',
    '--property:WindowsAppSDKSelfContained=true',
    '--property:PublishReadyToRun=false'
)

# dotnet publish does not carry the WinUI-generated XBF/PRI files from the
# regular build output into a custom publish directory. They are required at
# runtime by Application.LoadComponent in an unpackaged WinUI application.
Copy-AppXamlResources -Source $appBuildOutput -Destination $appPublish

Invoke-Dotnet @(
    'publish', $serviceProject,
    '--configuration', $Configuration,
    '--framework', 'net10.0-windows10.0.19041.0',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $servicePublish,
    '--property:Platform=x64'
)

Copy-PublishTree -Source $appPublish -Destination $appPayload
Copy-PublishTree -Source $servicePublish -Destination $servicePayload
Prepare-MihomoPayload

$requiredPayloadFiles = @(
    (Join-Path $appPayload 'ClashTray.App.exe'),
    (Join-Path $servicePayload 'ClashTray.Service.exe'),
    (Join-Path $corePayload 'mihomo.exe'),
    (Join-Path $corePayload 'Mihomo-LICENSE.txt')
)
foreach ($requiredFile in $requiredPayloadFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Published payload is missing required file: $requiredFile"
    }
}

Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $payloadZip -CompressionLevel Optimal -Force
if (-not (Test-Path -LiteralPath $payloadZip -PathType Leaf)) {
    throw "Payload archive was not created: $payloadZip"
}

Invoke-Dotnet @(
    'publish', $setupProject,
    '--configuration', $Configuration,
    '--framework', 'net10.0-windows10.0.19041.0',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $setupPublish,
    '--property:Platform=x64',
    "--property:Version=$PackageVersion",
    "--property:FileVersion=$PackageVersion",
    "--property:AssemblyVersion=$PackageVersion",
    '--property:PublishSingleFile=true',
    '--property:IncludeNativeLibrariesForSelfExtract=true',
    '--property:EnableCompressionInSingleFile=true'
)

$publishedSetup = Join-Path $setupPublish 'ClashTray.Setup.exe'
if (-not (Test-Path -LiteralPath $publishedSetup -PathType Leaf)) {
    throw "Published setup executable was not created: $publishedSetup"
}

$installerPath = Join-Path $outputRoot 'ClashTray-Setup.exe'
$hashPath = Join-Path $outputRoot 'ClashTray-Setup.sha256'
if (Test-Path -LiteralPath $installerPath) {
    Remove-Item -LiteralPath $installerPath -Force
}
if (Test-Path -LiteralPath $hashPath) {
    Remove-Item -LiteralPath $hashPath -Force
}
Copy-Item -LiteralPath $publishedSetup -Destination $installerPath -Force

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$hash *ClashTray-Setup.exe" -Encoding ascii

$installer = Get-Item -LiteralPath $installerPath
$archive = Get-Item -LiteralPath $payloadZip
Write-Host "Installer: $($installer.FullName)"
Write-Host "Installer size: $([math]::Round($installer.Length / 1MB, 2)) MiB"
Write-Host "Installer SHA-256: $hash"
Write-Host "Embedded payload archive: $($archive.Length) bytes"
