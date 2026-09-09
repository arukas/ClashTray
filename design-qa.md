# Compact Windows flyout visual checkpoint

final result: passed

Scope: visual review of the revised native WinUI main panel. This is not a claim of complete product, installer, live proxy or Explorer interaction acceptance.

- Source visual truth: C:/Users/Zen/AppData/Local/Temp/codex-clipboard-e60601f6-0a7c-43df-b96f-c8a654a59728.png (ClashBar); user-supplied current app and Bluetooth screenshots establish layout defects and anchoring intent.
- Implementation screenshots: D:/ClashTray/artifacts/ui-v5/nodes-dark.png, nodes-light.png, empty-dark.png, empty-light.png.
- Viewport: 420 x 680 effective pixels at 100%, PNGs 420 x 680.
- Reference was displayed at 420 pixels wide with its original aspect ratio; reference includes black surrounding canvas and is taller. These are intentional native Windows size/content differences, not pixel-identical cloning.
- State: empty/stopped and synthetic populated/running examples. Different provider/group counts and traffic values intentionally demonstrate layout; no subscription quotas or node data are fabricated in production.
- Full-view comparison: normalized reference plus both latest theme screenshots displayed in the same comparison input. Header, modes, text tabs, metrics, config, independent capture switches, provider and group list ordering agree.
- Focused comparison: at 420-pixel width, text, switches, current-node labels and delays are directly readable in the full captures; no extra crop needed.

## Iteration history

1. v3: dark secondary node text was too dark; default expanders consumed most node area. P1/P2.
2. v4: secondary text moved to XAML ThemeResource style; group cards replaced by borderless rows; top/quick controls shortened. Two-line groups still showed too few rows. P2.
3. v5: single-line group name/current node/delay/expand/test arrangement. Four sample groups fit above the footer. Both themes re-rendered and reviewed; no remaining P0/P1/P2 visual findings in this scope.

## Required surfaces

- Typography: Segoe/Windows fallback and native Fluent icons, 22px app title, 14-15px controls/group names, 12px secondary values. Long labels ellipsize.
- Spacing: continuous background, no stack of oversized cards, consistent gutters, right-aligned independent switches and footer. Configuration actions are in a flyout.
- Colors: theme resources follow light/dark/system and accent; corrected dark secondary contrast. High-contrast OS rendering still needs manual verification.
- Assets: original ClashTray icon retained, no copied ClashBar mascot/brand. Traffic graph is rendered from bounded real production metrics.
- Copy: Chinese Windows terminology and explicit System Proxy/TUN labels; placeholders are confined to diagnostic samples.

## Remaining verification

- Native window geometry and in-process visibility/dismissal passed; DWM corners/shadows not present in RenderTargetBitmap captures.
- Actual Explorer click delivery, real mixed-DPI monitor transitions, config flyout focus and live proxy actions remain to be exercised.
- User accepted this checkpoint and requested commit; installer generation deferred.
