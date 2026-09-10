# ClashTray development setup

## Required components

- Windows 10 22H2 or Windows 11
- .NET SDK 10.0.400 or later
- Visual Studio 2026 with the Windows App SDK/WinUI tooling

The repository pins Windows App SDK 2.4.0 and test packages in `Directory.Packages.props`.

## Build and test

From the repository root:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' restore ClashTray.sln
& 'C:\Program Files\dotnet\dotnet.exe' build ClashTray.sln --configuration Debug --property:Platform=x64
& 'C:\Program Files\dotnet\dotnet.exe' test ClashTray.sln --configuration Debug --property:Platform=x64 --no-build
```

The first launch creates user data in `%LOCALAPPDATA%\ClashTray` and runtime data in `%PROGRAMDATA%\ClashTray`. A Release EXE installation includes the pinned official Mihomo v1.19.30 x64 core under `%PROGRAMDATA%\ClashTray\core`; a plain developer launch still needs a core at `%LOCALAPPDATA%\ClashTray\core\mihomo.exe` or an update through the verified update flow.

The Windows Service is optional during ordinary development. If it is installed, the app sends core lifecycle and TUN requests through its restricted pipe; otherwise core lifecycle falls back to the desktop process and TUN reports that the service is unavailable.

## EXE installer (current distribution path)

The current release path is a set of x64 EXE installers; Full is the recommended self-contained variant and NoCET is the compatibility variant for older-patched Windows 10 22H2 systems. It does not use MSIX, AppX signing, or the Windows Store. Build it from the repository root:

```powershell
& .\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0 -Variant Full
& .\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0 -Variant NoCET
```

The output is:

- `packaging\out\ClashTray-Setup-Full.exe`: one self-contained installer containing the desktop app, Windows Service, official Mihomo v1.19.30 x64 core, and its license notice.
- `packaging\out\ClashTray-Setup-Full.sha256`: SHA-256 sidecar file.
- `packaging\out\ClashTray-Setup-NoCET.exe`: a self-contained Full-equivalent installer built with `CETCompat=false` for older-patched Windows 10 22H2.
- `packaging\out\ClashTray-Setup-NoCET.sha256`: SHA-256 sidecar file for the compatibility installer.

The installer requests administrator approval through its manifest. Double-clicking it should show the UAC prompt; the installed desktop app subsequently runs with ordinary user permissions. If Explorer does not show a “Run as administrator” context-menu item, launch it from any PowerShell window with:

```powershell
Start-Process -FilePath 'D:\ClashTray\packaging\out\ClashTray-Setup-Full.exe' -Verb RunAs
```

After approving UAC, the installer places files under `C:\Program Files\ClashTray`, installs the bundled core under `%PROGRAMDATA%\ClashTray\core`, registers and starts `ClashTrayService`, creates a Start Menu shortcut, starts the tray app, and writes the normal Windows uninstall entry. The service pipe is ACL-restricted to the installing user, LocalSystem, and Administrators.

For a first manual verification:

1. Quit any running ClashTray instance from its tray menu.
2. Run `ClashTray-Setup-Full.exe` and approve the UAC prompt.
3. Start **ClashTray** from the Start Menu.
4. Confirm the tray icon appears, open the panel, and verify that TUN no longer reports “service unavailable”.
5. The installer already provides Mihomo v1.19.30; import a YAML configuration and test start/stop, mode switching, System Proxy, and TUN. Use NoCET only when the normal Full installer cannot start on an older-patched Windows 10 22H2 system.
6. Confirm the service state from an elevated PowerShell window:

```powershell
Get-Service -Name ClashTrayService
```

To upgrade, quit ClashTray and run the new installer from a location outside `C:\Program Files\ClashTray`. The installer swaps the application directory atomically and preserves user data. To uninstall, use **Settings > Apps > Installed apps > ClashTray > Uninstall**; the prompt lets you keep or delete configuration, subscription, and log data. Uninstall stops the service and restores ClashTray-owned System Proxy state before removing the program files.

Unsigned EXE builds do not need an MSIX certificate, but Windows may show an unknown-publisher/SmartScreen warning. A trusted Authenticode certificate can be added later as a distribution step; no certificate is required for local installation.

## Legacy MSIX files

`Build-MSIX.ps1` and `packaging\ClashTray.Package` are retained as historical packaging material. They are not part of the current install path. Do not use the old unsigned `.msix` for manual verification; use `Build-EXE.ps1` and `ClashTray-Setup-Full.exe` instead.

## Release safety

Test invalid YAML, unavailable subscriptions, controller/port conflicts, Mihomo crash/restart, service restart, System Proxy ownership conflicts, TUN failures, Explorer restart, multiple monitors, DPI scaling, dark/light/high-contrast themes, clean upgrade, and uninstall restoration before publishing a release.

## Build variants and compressed release packages

The recommended release path is `packaging/Build-EXE.ps1`. It creates a compressed single-file installer with an embedded ZIP payload (Framework uses a framework-dependent host, while its payload remains compressed):

```powershell
.\packaging\Build-EXE.ps1 -Variant Full -PackageVersion 1.0.0
.\packaging\Build-EXE.ps1 -Variant NoCET -PackageVersion 1.0.0
.\packaging\Build-EXE.ps1 -Variant NoCore -PackageVersion 1.0.0
.\packaging\Build-EXE.ps1 -Variant Framework -PackageVersion 1.0.0
```

`Full` is self-contained and includes the pinned, SHA-256 verified Mihomo core. `NoCET` is the same bundled-core shape with `CETCompat=false` for older-patched Windows 10 22H2. `NoCore` is self-contained but leaves the core to the verified in-app updater. `Framework` omits the core and depends on the .NET 10 Desktop Runtime and Windows App SDK runtime already being installed. Outputs are named `ClashTray-Setup-Full.exe`, `ClashTray-Setup-NoCET.exe`, `ClashTray-Setup-NoCore.exe`, and `ClashTray-Setup-Framework.exe`, with a matching `.sha256` sidecar. See [release.md](release.md) for the release matrix and checks.
