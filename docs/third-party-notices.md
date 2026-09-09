# Third-party notices

ClashTray controls the separately distributed [Mihomo](https://github.com/MetaCubeX/mihomo) core and does not embed or copy its source. The Full Windows EXE installer bundles the official `mihomo-windows-amd64-v1.19.30.zip` binary without source changes and installs it beside the corresponding `Mihomo-LICENSE.txt` and release metadata under `%PROGRAMDATA%\ClashTray\core`. NoCore and Framework installers intentionally omit the binary; the app can obtain it through the verified updater.

The desktop UI uses Microsoft Windows App SDK and WinUI 3 through NuGet. The package's license and notice files remain governed by the corresponding Microsoft package distribution and should be included in the release artifact when required by the selected distribution channel.

ClashTray itself does not add telemetry, analytics, account services, advertisements, or remote dashboard code.

## ClashBar acknowledgment

ClashTray also thanks [Sitoi/ClashBar](https://github.com/Sitoi/clashbar) for demonstrating a compact, menu-bar-first proxy workflow. ClashTray is an independent Windows implementation and does not redistribute ClashBar source code or assets. This acknowledgment does not change ClashTray's MIT license.
