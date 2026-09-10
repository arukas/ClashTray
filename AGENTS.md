# ClashTray Repository Instructions

This file is the persistent implementation brief for agents working in this repository. Read it before planning or changing files.

## DEFAULT IMPLEMENTATION REQUIREMENT

This repository is a greenfield application project. The default product requirement is complete and is defined in this file.

**Required deliverable:** build the Windows-only ClashTray daily-usable v1 application, including every item marked P0 and P1 below. P2 is excluded unless the user explicitly requests it.

When the user says any equivalent of the following, treat it as an explicit implementation request and begin work immediately:

- "Develop this project."
- "Start coding."
- "Implement the app."
- "Build it according to AGENTS.md."
- "Continue the implementation."
- "按照 AGENTS.md 开发。"
- "开始开发。"

Do **not** respond that there are no requirements, no specification, no design brief, or no task. This file contains the requirements, technology decision, product behavior, scope, implementation order, and definition of done.

Do not require a separate PRD, mockup, architecture document, or task list before starting. Use the requirements and defaults in this file. If a small detail is unspecified, choose the safest conventional Windows behavior, record the assumption, and continue. Stop for clarification only when a decision would materially change security, distribution, data loss risk, or the P0/P1 product scope.

When implementation is authorized, the first work item is:

1. Inspect the repository and installed development environment.
2. If required tools are present, create the solution and project structure described below.
3. Establish a successful restore, build, and unit-test baseline.
4. Implement the tray icon, anchored flyout, single-instance behavior, and explicit Quit flow as the first vertical slice.
5. Continue through the ordered implementation plan in this file until all P0 and P1 acceptance requirements are met, unless the user narrows or pauses the work.

The application must be functional. A static UI mock, design-only prototype, browser dashboard wrapper, or collection of placeholder screens does not satisfy the requirement.

## Agent and execution policy

- Use `gpt-5.6-luna` with reasoning effort `max` for implementation and coding work in this repository.
- A user request to plan, explain, review, or edit documentation does not authorize implementation. A request to build, implement, start, continue, or develop according to this file does authorize the full P0+P1 implementation described here.
- Once implementation is authorized, proceed from this brief without asking what product to build. Ask only when a missing decision would materially change scope, security, distribution, or user-visible behavior.
- The latest explicit user instruction overrides this file.
- Preserve unrelated user changes. Never use destructive Git or filesystem operations without explicit approval.

## Product objective

Build **ClashTray**, a lightweight, Windows-only, tray-first desktop client powered by the Mihomo core.

The product is functionally inspired by [Sitoi/ClashBar](https://github.com/Sitoi/ClashBar), but it must be an independent Windows-native implementation:

- Do not port or copy ClashBar's Swift source code.
- Do not translate, rewrite, or mechanically convert ClashBar GPL source files into C#. Study GPL projects only for public behavior, protocol requirements, and user-experience concepts; implement the feature independently and review any proposed source reuse before incorporating it.
- Do not copy its name, logo, icons, screenshots, or proprietary-looking visual assets.
- Public behavior and workflows may be used as a functional reference.
- Use original Windows Fluent styling and original product assets.
- Preserve Mihomo license notices and third-party attributions.

The intended user experience is:

1. The app starts quietly in the Windows notification area.
2. Left-clicking the tray icon opens a compact control panel next to the taskbar icon.
3. The user can import a Mihomo configuration or subscription, start the core, choose a proxy mode and node, test latency, and enable System Proxy or TUN.
4. Rules, active connections, traffic, memory, and logs can be inspected without opening a browser dashboard.
5. Closing the panel returns the app to the tray. Explicit **Quit** exits the app.

## Supported platform

- Windows only.
- Primary target: Windows 11.
- Compatibility target: Windows 10 22H2 where supported by the chosen Windows App SDK and packaging features.
- Primary architecture: x64.
- ARM64 is deferred unless the user explicitly requests it.
- Do not introduce macOS, Linux, mobile, or web-app targets.

## Required technology direction

- Language/runtime: C# on .NET 10 LTS.
- UI: WinUI 3 with the latest stable Windows App SDK compatible with .NET 10.
- Design: Windows Fluent controls, system theme, system accent color, Segoe UI Variable, high-contrast support, and DPI-aware layout.
- Tray integration: Win32 notification-area APIs, including stable icon identity and tray-icon rectangle lookup for flyout placement.
- Core: official Mihomo Windows binary. Treat Mihomo as a separate managed process, not an embedded library.
- Core control: Mihomo REST API and WebSocket streams through a loopback-only External Controller.
- Privileged operations: a small Windows Service communicating with the desktop app through a restricted named pipe.
- Packaging: MSIX first, including the per-machine service where supported. Reconsider packaging only if a proven platform limitation blocks the required behavior.
- Tests: a normal .NET unit-test project plus integration tests for process, API, proxy-state, and service boundaries.

Do not replace this stack with Electron, Tauri, WPF, WinForms, MAUI, Rust, Go, Node.js, or a browser-based UI unless the user explicitly changes the decision.

## Suggested solution structure

Keep UI, domain logic, privileged operations, and tests separated. The exact names may be adjusted when necessary, but use this general layout:

```text
ClashTray.sln
src/
  ClashTray.App/          WinUI tray application and presentation layer
  ClashTray.Core/         domain models, Mihomo API client, config and state logic
  ClashTray.Service/      Windows Service and privileged command handlers
  ClashTray.Contracts/    narrow IPC request/response contracts
tests/
  ClashTray.Core.Tests/
  ClashTray.IntegrationTests/
packaging/
  ClashTray.Package/      MSIX manifest and release packaging
docs/
```

Keep dependencies flowing inward: App and Service may depend on Core/Contracts; Core must not depend on the UI or Service.

## Version-one scope

Unless the user explicitly selects a smaller prototype or the advanced scope, implement the **daily-usable v1 scope** below.

### P0: complete working path

- Single-instance tray application.
- Tray icon states for stopped, running, System Proxy, TUN, and error.
- Left-click flyout and right-click quick-action menu.
- Core discovery, version display, configuration validation, start, stop, restart, and crash detection.
- Import local `.yaml` and `.yml` configurations.
- Import subscription URLs with optional local name.
- Select, reload, refresh, and delete configurations.
- Mihomo modes: `rule`, `global`, and `direct`.
- Proxy groups, current selections, node switching, and latency tests.
- System Proxy enable/disable with bypass-list support.
- Original Windows proxy state backup and reliable restoration.
- Current upload/download rate, total traffic, connection count, and memory status.
- Basic bounded application and Mihomo logs.
- Start with Windows and start core automatically settings.
- Light, dark, system, high-contrast, and common DPI scaling behavior.

### P1: daily-usable v1 completion

- TUN enable/disable through the privileged service.
- Proxy Provider and Rule Provider status and manual refresh.
- Rules list with search and filters.
- Connections list with search, sorting, details, close-one, and close-all actions.
- Live logs with source/level filters, search, copy, clear, and bounded retention.
- HTTP, SOCKS, and Mixed port settings.
- `allow-lan`, IPv6, TCP concurrent, and log-level settings.
- Clear DNS and FakeIP cache actions.
- Geo database update action.
- Scheduled subscription refresh.
- Mihomo core update with verified provenance, checksum validation, atomic replacement, and rollback.
- Clean install, upgrade, and uninstall behavior.

### P2: deferred unless explicitly requested

- Wi-Fi/SSID-based configuration switching.
- Remote Mihomo endpoint management.
- Portable/no-core distributions.
- ARM64 builds.
- Historical traffic accounting and analytics.
- Broad global keyboard shortcut customization.
- Additional languages beyond Simplified Chinese and English.

Do not silently expand P0/P1 work into P2.

## UI behavior specification

- The default panel should be approximately 420 by 640 effective pixels and clamp to the active monitor work area.
- Position the panel relative to the real tray icon rectangle and account for taskbars on any screen edge.
- The default flyout must not appear in Alt+Tab or as a normal taskbar window.
- Clicking outside closes an unpinned flyout. A pin action converts it into a persistent, resizable window.
- The header shows core state, active configuration, traffic rate, connection count, and primary quick controls.
- Separate **System Proxy** and **TUN** controls. They are different traffic-capture mechanisms and must never be represented as one ambiguous switch.
- Provide five primary destinations: Proxy, Rules, Connections, Logs, and Settings.
- The right-click tray menu provides System Proxy, TUN, Rule/Global/Direct, Start/Restart, Stop, Open Panel, and Quit.
- UI state must be derived from confirmed OS/core state. Do not optimistically show a switch as enabled before the operation succeeds.
- Use plain Windows terminology in user-visible text. Avoid macOS terminology such as menu bar, Finder, login item, or Command-key shortcuts.
- Use accessible names, keyboard navigation, visible focus, scalable text, and non-color-only status indicators.

## Process and privilege architecture

Use three trust boundaries:

1. **Desktop app**: runs as the signed-in user and owns UI, subscriptions, per-user settings, System Proxy, and notifications.
2. **Windows Service**: runs with the minimum privileges required to own Mihomo lifecycle and TUN operations. It exposes only an allow-listed IPC command surface.
3. **Mihomo**: runs as a child controlled by the service and exposes its controller only on loopback.

Security requirements:

- Bind the Mihomo External Controller to `127.0.0.1`, never `0.0.0.0` by default.
- Generate a strong per-install controller secret. Never commit secrets or subscription URLs.
- Restrict the service named pipe with explicit ACLs.
- Never allow arbitrary executable paths, arbitrary shell commands, or unrestricted file writes through IPC.
- Store per-user secrets using Windows-supported protected storage such as DPAPI/Credential Locker.
- Place user settings and subscription metadata under `%LOCALAPPDATA%\ClashTray`.
- Place service-owned runtime data under `%PROGRAMDATA%\ClashTray` with explicit restricted ACLs.
- Ensure active configuration files containing credentials are readable only by the intended user, Administrators, and the service identity.

## Windows System Proxy requirements

- System Proxy is per-user and normally does not require elevation.
- Before changing it, capture all relevant original proxy values, not only the enable flag.
- Apply the local Mihomo HTTP or Mixed endpoint and a configurable bypass list.
- Notify Windows/WinINet after changes so applications observe the new state.
- Restore the previous state when the user disables System Proxy, when startup recovery detects stale ownership, and during uninstall.
- Do not overwrite proxy settings changed by another application after ClashTray enabled them. Track ownership and compare before restoring.
- Detect competing Clash/Mihomo clients and port conflicts where practical; warn instead of silently taking over.

## TUN and service requirements

- Installing the service may request UAC once. Routine UI use must remain non-elevated.
- TUN state must be confirmed from the running Mihomo configuration/status, not inferred from the last button press.
- Enabling or disabling TUN must be serialized with core restart operations.
- Failure must leave the machine with a usable route and DNS configuration.
- Handle sleep/resume, network changes, service restart, app restart, and Mihomo crash.
- Uninstall must stop Mihomo, disable TUN, restore owned network state, and remove the service.

## Mihomo integration requirements

- Download cores only from the official MetaCubeX/Mihomo release source.
- Pin an explicitly tested Mihomo version for releases; do not consume an unpinned `latest` asset at runtime.
- Verify download status, architecture, archive contents, and SHA-256 before replacement.
- Validate configuration using Mihomo before starting it.
- Reconnect REST/WebSocket consumers after core restart or endpoint change with bounded exponential backoff.
- Use structured logs when available.
- Bound traffic, connection, and log buffers. Apply backpressure, rate limiting, and repeated-line folding so a noisy core cannot exhaust memory or the UI thread.
- Treat API responses, YAML content, provider metadata, node names, and remote icons as untrusted input.

## State management requirements

Model important operations as explicit state machines instead of unrelated booleans. At minimum, represent:

- Core: missing, stopped, validating, starting, running, stopping, restarting, failed.
- System Proxy: off, enabling, on, disabling, restore-required, failed.
- TUN: unavailable, off, enabling, on, disabling, failed.
- Subscription: idle, downloading, validating, applying, succeeded, failed.

Serialize conflicting operations. Cancellation, timeout, retry, and rollback behavior must be deliberate and tested.

## Implementation order

When the user authorizes the full implementation, work in vertical slices in this order:

1. Create the solution structure, shared conventions, and tests.
2. Prove the tray icon, anchored flyout, single-instance behavior, and clean exit.
3. Prove Mihomo validation/start/stop and loopback API connectivity with a minimal safe config.
4. Implement config/subscription management and the Proxy page.
5. Implement mode switching, node switching, and latency testing.
6. Implement reversible System Proxy ownership and recovery.
7. Add the service and TUN path.
8. Add Rules, Connections, live metrics, and bounded Logs.
9. Add settings, maintenance, core update, packaging, and update/uninstall recovery.
10. Complete compatibility, security, accessibility, and release testing.

Do not build every screen as a static shell before proving the end-to-end core path.

## Coding standards

- Enable nullable reference types and treat new compiler warnings seriously.
- Prefer asynchronous APIs for process, file, HTTP, WebSocket, and IPC operations.
- Pass cancellation tokens through long-running operations.
- Keep UI-thread work small; parse and aggregate high-volume data off the UI thread.
- Use dependency injection at application boundaries, but avoid abstraction layers that have no second implementation or testing value.
- Keep models immutable where practical.
- Use structured logging and redact secrets, authorization headers, subscription URLs, and proxy credentials.
- Do not swallow exceptions. Convert expected failures into typed results and actionable user-visible errors.
- Avoid adding dependencies for functionality available reliably in the .NET or Windows SDK.
- Pin dependency versions through central package management once the solution exists.
- Do not add telemetry, analytics, crash upload, advertisements, or account systems unless the user explicitly requests them.

## Testing and completion rules

For every implemented slice:

- Build the complete solution.
- Run all relevant unit and integration tests.
- Exercise the affected user flow manually when Windows UI or networking behavior changed.
- Report exactly what was verified and what could not be verified.

Required release scenarios include:

- Clean install and first launch.
- Upgrade over an older version.
- Uninstall with restore-and-keep-data choices.
- Invalid YAML and unavailable subscription URL.
- Port/controller conflicts.
- Core crash and log flood.
- UI restart while the service/core remains running.
- Service restart while the UI is running.
- System Proxy ownership conflict with another app.
- TUN enable/disable failure and rollback.
- Sleep/resume and network adapter changes.
- Explorer restart and tray icon recreation.
- Multiple monitors, taskbar positions, high DPI, dark mode, and high contrast.

An implementation is not complete merely because it compiles. The requested flow must work and failure paths must preserve network connectivity.

## Environment handling

- Before coding, check whether Visual Studio, .NET 10 SDK, the Windows SDK, and required WinUI/MSIX workloads are available.
- If a required tool is missing, stop before scaffolding and give the user the exact missing component and installation path.
- Do not install system software, enable Windows features, request certificates, or change machine-wide settings without explicit user authorization.
- NuGet/project-local dependencies may be restored as a normal build step after implementation is authorized.
- Do not require Node.js, Python, Go, Rust, Java, Docker, or WSL for the chosen implementation.

## Source control and handoff

- Keep commits and diffs scoped to the active task.
- Never discard, reset, or rewrite unrelated user work.
- Do not commit generated secrets, certificates, build outputs, Mihomo runtime data, subscription files, or user logs.
- Update documentation when behavior or setup changes.
- In every completion report, lead with the working result, list verification performed, identify remaining limitations, and link to the important changed files.
