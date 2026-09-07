# ClashTray development setup

## Required components

- Windows 10 22H2 or Windows 11
- .NET SDK 10.0.400 or later
- Visual Studio 2026 with the Windows App SDK/WinUI tooling
- Windows SDK 10.0.26100.0, including MakeAppx and SignTool

The repository pins Windows App SDK 2.4.0 and test packages in `Directory.Packages.props`.

## Build and test

From the repository root:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' restore ClashTray.sln
& 'C:\Program Files\dotnet\dotnet.exe' build ClashTray.sln --configuration Debug --property:Platform=x64
& 'C:\Program Files\dotnet\dotnet.exe' test ClashTray.sln --configuration Debug --property:Platform=x64 --no-build
```

The first launch creates user data in `%LOCALAPPDATA%\ClashTray` and runtime data in `%PROGRAMDATA%\ClashTray`. Put an official x64 `mihomo.exe` at `%LOCALAPPDATA%\ClashTray\core\mihomo.exe` for local development, or install it through the verified update flow.

The packaged service is optional during ordinary development. If it is installed, the app sends core lifecycle and TUN requests through its restricted pipe; otherwise core lifecycle falls back to the desktop process and TUN reports that the service is unavailable.

## MSIX

Run the following from the repository root in an elevated developer shell when packaging is permitted:

```powershell
& .\packaging\Build-MSIX.ps1 -Configuration Release
```

This creates an unsigned x64 MSIX under `packaging\out`. To sign a release, pass `-CertificatePath` for a certificate managed outside the repository. The package manifest includes the Windows packaged-service extension, so installation and removal are owned by MSIX when the target Windows edition grants the restricted capability.

For an upgrade build, pass a higher four-part package version, for example `-PackageVersion 0.1.1.0`. Keep the package version monotonically increasing so Windows recognizes the artifact as an upgrade.

## Release safety

Test invalid YAML, unavailable subscriptions, controller/port conflicts, Mihomo crash/restart, service restart, System Proxy ownership conflicts, TUN failures, Explorer restart, multiple monitors, DPI scaling, dark/light/high-contrast themes, clean upgrade, and uninstall restoration before publishing a release.
