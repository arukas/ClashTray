# ClashTray Branding Assets

This directory records the production-ready raster and vector assets supplied in
`ClashTray-Branding-Assets-v1.zip`, together with their repository mapping.

The Markdown, JSON, and font notice files in this directory are asset metadata and
licensing notes. They do not override the repository's implementation instructions.

## Repository mapping

- Application icon: `src/ClashTray.App/Assets/App/ClashTray.ico`
- Application PNG sizes: `src/ClashTray.App/Assets/App/png/`
- Panel and wordmark assets: `src/ClashTray.App/Assets/Branding/`
- Light-taskbar tray assets: `src/ClashTray.App/Assets/Themes/Light/Tray/`
- Dark-taskbar tray assets: `src/ClashTray.App/Assets/Themes/Dark/Tray/`
- README banner and GitHub preview: `.github/assets/`
- Source artwork and preview sheets: `docs/branding/source/` and `docs/branding/preview/`

## Folder layout

- `source/`
  - Original tray-cat artwork and extracted alpha mask.
- `branding/`
  - Horizontal logo variants, transparent logo marks, and SVG vectorized marks.
- `icons/app/`
  - Windows application icon PNGs and a multi-size `ClashTray.ico`.
- `icons/tray/light-taskbar/`
  - Tray icons optimized for light Windows taskbars.
- `icons/tray/dark-taskbar/`
  - Tray icons optimized for dark Windows taskbars.
- `github/`
  - README banner and GitHub social preview.
- `preview/`
  - Original branding concept sheet used as the visual reference.

## Recommended usage

### Windows EXE / shortcut icon
Use:
`icons/app/ico/ClashTray.ico`

It includes common Windows icon sizes from 16×16 through 256×256.

### System tray
Switch icon family according to Windows theme:

- Light taskbar → `icons/tray/light-taskbar/`
- Dark taskbar → `icons/tray/dark-taskbar/`

State naming:

- `Disconnected`
- `Connecting`
- `Connected`
- `Error`
- `Paused`

### GitHub
- README header: `github/ClashTray-README-Banner-1600x500.png`
- Social preview: `github/ClashTray-GitHub-SocialPreview-1280x640.png`

## Typography

The raster wordmarks use **Inter**.

No font files are bundled in this package.

Inter is distributed under the SIL Open Font License 1.1 and is suitable for commercial/open-source branding use.

For Chinese UI/documentation, **Noto Sans CJK SC** / **Noto Sans SC** is recommended. Noto fonts are also distributed under the SIL Open Font License 1.1.

## Brand colors

- Navy: `#0F172A`
- Windows Blue: `#0078D4`
- Light Blue: `#38BDF8`
- Slate Gray: `#94A3B8`
- Connected Green: `#22C55E`
- Error Red: `#EF4444`
- Paused Amber: `#F59E0B`

## Notes

The tray icon intentionally remains simpler than the application icon so it stays legible at 16–24 px. Each state ICO contains 16, 20, 24, and 32 px resources; the Win32 loader selects the DPI-aware small-icon dimensions instead of loading the largest resource and relying on shell scaling.
The panel keeps the Logo button's 40 px hit target while rendering the existing transparent-safe-area artwork at that size, making the visible mark approximately 24–26 px.
The Windows identity is expressed through Fluent-style geometry, taskbar/tray metaphors, and Windows blue rather than by reproducing the Microsoft Windows trademark.
