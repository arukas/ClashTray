# Third-party notices

## Mihomo

ClashTray can control the separately executed [Mihomo](https://github.com/MetaCubeX/mihomo) core.

Mihomo is licensed under the GNU General Public License v3.0.

ClashTray does not incorporate Mihomo source code into the ClashTray application or service. Mihomo runs as a separate executable and ClashTray communicates with it through its External Controller HTTP/WebSocket interface and normal process-management interfaces.

The ClashTray Full and NoCET installers redistribute an official, unmodified Mihomo Windows binary. Each bundled distribution includes:

- the Mihomo GPLv3 license text;
- the exact Mihomo version;
- the SHA-256 of the redistributed binary archive;
- a Corresponding Source link for that exact version;
- a downloadable source archive where provided by the release process.

The installed `Mihomo-Release.txt` records the binary archive URL, binary checksum, exact Corresponding Source URL, source archive URL, upstream project, architecture, and the independent license boundary. The Mini installer intentionally omits the binary; the app can obtain it through the verified updater.

Mihomo remains governed by the GNU GPL v3.0. ClashTray remains governed by the MIT License.

## MetaCubeXD

ClashTray includes an unmodified, pinned static release of [MetaCubeXD](https://github.com/MetaCubeX/metacubexd) and serves it only through Mihomo's loopback External Controller.

The current release payload is MetaCubeXD `v1.273.1`, downloaded from the official release asset `compressed-dist.tgz` and verified with the SHA-256 recorded in `packaging/metacubexd-release.json`. The installer includes the MetaCubeXD MIT license text and exact source/download metadata under the installed ProgramData UI directory.

ClashTray does not load a remote dashboard at runtime and does not enable LAN access or add the controller secret to the dashboard URL. MetaCubeXD remains governed by its upstream MIT license.

## Microsoft Windows App SDK and WinUI 3

The desktop UI uses Microsoft Windows App SDK and WinUI 3 through NuGet. The package's license and notice files remain governed by the corresponding Microsoft package distribution and should be included in release artifacts when required by the selected distribution channel.

## ClashBar acknowledgment

ClashTray acknowledges [Sitoi/ClashBar](https://github.com/Sitoi/clashbar) for demonstrating a compact, menu-bar-first proxy client workflow.

ClashTray is an independently implemented Windows application. ClashBar source code and assets are not included in or distributed as part of ClashTray. ClashTray does not claim ownership of ClashBar source code or assets; ClashBar remains governed by its own upstream license.

This acknowledgment does not alter the MIT license of independently implemented ClashTray code.

## Project policy

ClashTray itself does not add telemetry, analytics, account services, or advertisements. The local MetaCubeXD dashboard is a separately licensed, pinned third-party release payload; ClashTray does not fetch dashboard code at runtime. See [development-policy.md](development-policy.md) for the source-authorship and integration rules used by the project.
