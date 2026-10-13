# Theme assets and Nakhimov unlock

- Light and Dark use `Assets/Themes/Light/logo.png` and `Assets/Themes/Dark/logo.png` for the panel.
- The notification-area cat icon uses the Windows taskbar light/dark preference independently of the panel theme. High contrast uses the system background color to select a legible monochrome icon.
- Nakhimov is initially hidden from both theme selectors. Click the panel Logo five times, leaving no more than 1.5 seconds between clicks. Clicking with the keyboard via the focused Logo button also works. Hiding the panel discards partial progress.
- Clicks three and four show a remaining-click hint. Click five saves `NakhimovUnlocked = true` and `Theme = "nakhimov"`, then activates the portrait Logo and a muted green dark palette. High-contrast brushes are unchanged.
- After unlocking, Nakhimov remains available in the header theme menu and Settings across restarts, even after choosing another theme. Five further clicks can activate it again.
- Theme changes do not restart Mihomo or change System Proxy or TUN settings. Failed settings writes do not commit the new in-memory theme.
- Original images and size-reference sheets are source material only. Build/publish includes the themed panel marks plus the light/dark taskbar tray icon families under `Assets/Themes/{Light,Dark}/Tray/`; source originals and preview images are excluded.

## Verification

The unit tests cover the fifth-click boundary, timeout and panel-close reset, rejection of a locked selection, saved-state reload, and switching back without losing the unlock.

The isolated `--ui-smoke-test=<absolute-directory>` flow invokes the real Logo button through WinUI automation, verifies four/five clicks and persisted settings, captures the Nakhimov panel, and verifies ordinary palette restoration. It uses isolated data and does not initialize Mihomo or modify proxy/TUN settings.

Physical mouse timing, system high-contrast changes, and the real notification-area icon remain manual checks.
