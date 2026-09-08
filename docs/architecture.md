# ClashTray architecture

ClashTray is a Windows-only tray application. The desktop process owns the WinUI 3 panel, per-user settings, subscriptions, System Proxy ownership, and the loopback REST client. Mihomo remains a separate `mihomo.exe` process.

The per-machine Windows Service is the privileged boundary. The EXE installer registers `ClashTrayService` as a LocalSystem service and passes the installing user's SID as a constrained startup argument. Its named pipe is `ClashTray.Service`; requests are JSON lines and the pipe ACL allows only that user, LocalSystem, and Administrators. The service accepts only the typed commands in `ClashTray.Contracts` and rejects paths that are not shaped as a ClashTray core/runtime path. It does not execute shell commands or accept arbitrary write targets.

The runtime configuration is generated under `%PROGRAMDATA%\ClashTray\runtime\mihomo\active-config.yaml`. It forces the loopback controller, the protected per-user controller secret, ports, LAN/IPv6/TCP settings, and log level. The app uses confirmed REST responses as the source of truth for core, TUN, traffic, providers, rules, and connections; service-hosted Mihomo logs are consumed from the authenticated logs WebSocket and desktop-hosted logs come from bounded process output.

System Proxy changes are reversible. The original registry values are saved before the first change and a second ownership marker records the exact proxy server and bypass list ClashTray wrote. Restoration is performed only while those values still match; if another application changed them, the UI reports `需要恢复` instead of overwriting the competing state.

The desktop process restores this state during normal quit and startup recovery. When the packaged service is stopping without a live desktop process, it also stops Mihomo, disables TUN, and restores matching ownership records for loaded user profiles before the service exits. This keeps uninstall and crash recovery from overwriting a competing proxy configuration.

The runtime buffers at most 500 UI log entries and folds adjacent identical lines. Controller responses, process log lines, and parsed collection counts are bounded before reaching the UI. Configuration and subscription data stay under `%LOCALAPPDATA%\ClashTray`; service-owned runtime data stays under `%PROGRAMDATA%\ClashTray`. Controller secrets are protected with Windows DPAPI.

The current release script builds self-contained x64 app/service outputs, downloads the pinned official Mihomo v1.19.30 Windows x64 archive with SHA-256 verification, includes its license notice, embeds all payloads into a single `ClashTray-Setup.exe`, and emits a SHA-256 sidecar file. The setup executable requests elevation only for installation, service registration, and uninstall; the installed desktop app remains unpackaged and runs as the signed-in user. No certificate, subscription URL, core archive, or generated runtime data belongs in source control.
