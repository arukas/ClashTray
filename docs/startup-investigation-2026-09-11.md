# Startup investigation — 2026-09-11

## Observed cause

After the Windows restart at 2026-09-10 20:01:34 (Asia/Singapore), the installed `ClashTrayService` was running with automatic startup, but neither the desktop app nor Mihomo was running. The per-user settings saved at 20:00:28 had both `startWithWindows` and `startCoreAutomatically` set to `false`. The current user's Windows Run key had no ClashTray entry. Starting the service alone does not request a core start; the desktop app applies the user's automatic-core preference.

The installed app reported product version `1.0.0+cf69b61430f3269635dfc1e6d1a7224f75d1277f`. The repository already contained commit `c95bcbc`, which preserves unsaved startup settings during live refresh. The installed app therefore did not contain that fix. The stored values establish why automatic startup did not occur; they do not establish exactly which UI actions occurred before the restart.

## Machine recovery status

A backup of the original settings and a temporary candidate settings file were created beside the per-user settings. The attempted atomic replacement failed before changing the active settings or startup registration. A subsequent recovery attempt was rejected by automatic approval because the review service had exhausted its usage allowance.

On resuming the investigation, the installed desktop app was already running (start time 2026-09-10 22:08:55), Mihomo was running, and its loopback `/version` endpoint returned `v1.19.30`. Both automatic-start preferences were still false and the Run entry was still absent. No successful machine startup repair or upgrade is implied by those healthy process checks.

## Verification

- Before new changes: complete Debug x64 solution build passed with zero warnings and errors.
- Before new changes: 68 core unit tests and 3 integration tests passed.
- Read-only live check: Mihomo controller responds; confirmed TUN state matches its saved enabled preference.
- The Windows desktop automation helper could not initialize because of a Windows sandbox setup error. Physical tray interaction and a fresh Windows sign-in have not been verified.

The user subsequently requested immediate commit and release without further local checks or tests. Consequently the baseline results above do not validate the new startup changes. The release uses the existing GitHub Actions workflow, which builds and tests before publishing its installer assets. No local installation or fresh Windows sign-in test was performed.
