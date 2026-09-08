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

The current release path is a self-contained x64 EXE installer. It does not use MSIX, AppX signing, or the Windows Store. Build it from the repository root:

```powershell
& .\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0
```

The output is:

- `packaging\out\ClashTray-Setup.exe`: one self-contained installer containing the desktop app, Windows Service, official Mihomo v1.19.30 x64 core, and its license notice.
- `packaging\out\ClashTray-Setup.sha256`: SHA-256 sidecar file.

The installer requests administrator approval through its manifest. Double-clicking it should show the UAC prompt; the installed desktop app subsequently runs with ordinary user permissions. If Explorer does not show a “Run as administrator” context-menu item, launch it from any PowerShell window with:

```powershell
Start-Process -FilePath 'D:\ClashTray\packaging\out\ClashTray-Setup.exe' -Verb RunAs
```

After approving UAC, the installer places files under `C:\Program Files\ClashTray`, installs the bundled core under `%PROGRAMDATA%\ClashTray\core`, registers and starts `ClashTrayService`, creates a Start Menu shortcut, starts the tray app, and writes the normal Windows uninstall entry. The service pipe is ACL-restricted to the installing user, LocalSystem, and Administrators.

For a first manual verification:

1. Quit any running ClashTray instance from its tray menu.
2. Run `ClashTray-Setup.exe` and approve the UAC prompt.
3. Start **ClashTray** from the Start Menu.
4. Confirm the tray icon appears, open the panel, and verify that TUN no longer reports “service unavailable”.
5. The installer already provides Mihomo v1.19.30; import a YAML configuration and test start/stop, mode switching, System Proxy, and TUN.
6. Confirm the service state from an elevated PowerShell window:

```powershell
Get-Service -Name ClashTrayService
```

To upgrade, quit ClashTray and run the new installer from a location outside `C:\Program Files\ClashTray`. The installer swaps the application directory atomically and preserves user data. To uninstall, use **Settings > Apps > Installed apps > ClashTray > Uninstall**; the prompt lets you keep or delete configuration, subscription, and log data. Uninstall stops the service and restores ClashTray-owned System Proxy state before removing the program files.

Unsigned EXE builds do not need an MSIX certificate, but Windows may show an unknown-publisher/SmartScreen warning. A trusted Authenticode certificate can be added later as a distribution step; no certificate is required for local installation.

## Legacy MSIX files

`Build-MSIX.ps1` and `packaging\ClashTray.Package` are retained as historical packaging material. They are not part of the current install path. Do not use the old unsigned `.msix` for manual verification; use `Build-EXE.ps1` and `ClashTray-Setup.exe` instead.

## Release safety

Test invalid YAML, unavailable subscriptions, controller/port conflicts, Mihomo crash/restart, service restart, System Proxy ownership conflicts, TUN failures, Explorer restart, multiple monitors, DPI scaling, dark/light/high-contrast themes, clean upgrade, and uninstall restoration before publishing a release.
