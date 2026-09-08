# Third-party notices

ClashTray controls the separately distributed [Mihomo](https://github.com/MetaCubeX/mihomo) core and does not embed or copy its source. The current Windows EXE installer bundles the official `mihomo-windows-amd64-v1.19.30.zip` binary without source changes and installs it beside the corresponding `Mihomo-LICENSE.txt` and release metadata under `%PROGRAMDATA%\ClashTray\core`.

The desktop UI uses Microsoft Windows App SDK and WinUI 3 through NuGet. The package's license and notice files remain governed by the corresponding Microsoft package distribution and should be included in the release artifact when required by the selected distribution channel.

ClashTray itself does not add telemetry, analytics, account services, advertisements, or remote dashboard code.
