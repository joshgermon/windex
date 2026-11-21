# Windex design specification

Status: v1.0, implementation-ready · 2026-11-18 · Product design

Windex is a native Windows 11 client for Rex (Superlogical's terminal multiplexer). A Rex server owns the terminals. Windex attaches to saved hosts (local, SSH, HTTPS, usually over Tailscale) and shows **host → session → tabs → split panes**. This document defines how it looks and behaves. It contains no app code, but every element maps to a named WinUI 3 control, theme resource or Windows API.

Stack: C# · WinUI 3 (Windows App SDK ≥ 1.8) · .NET 10 · Win2D terminal surface (`CanvasSwapChainPanel`).

Measurements are in effective pixels (epx): XAML units at 100% scale. Colours are `#AARRGGBB` when they have alpha and `#RRGGBB` otherwise.

---

> ## Amendments by the orchestrator (2026-11-18) — these override the sections they name
>
> The user asked for the app to *feel like Rex on Mac* while using Windows primitives. Two changes
> bring it closer to Rex's identity:
>
> 1. **Tabs are Rex-style glass pills, not connected tabs** (overrides §3.4 "Tab looks", §0 "Title bar",
>    checklist item 4). Keep `TabView` for its behaviour (reorder, drag, keyboard, overflow scrolling,
>    add button) but restyle `TabViewItem` into floating pills in the title row:
>    height 32 epx, centred vertically in the 48 epx row, `CornerRadius` 8, 4 epx gap between pills,
>    padding 10,0,6,0. Selected pill: a glass chip — `RexChipSelectedBrush` (dark: #FFFFFF at 9%,
>    light: #FFFFFF at 72%) with a 1 epx gradient hairline (`RexChipStrokeBrush`: white 18%→6% top→bottom
>    in dark; black 8%→4% in light) and a soft 2 epx shadow in light mode. Unselected: transparent,
>    label `TextFillColorSecondaryBrush`; hover `SubtleFillColorSecondaryBrush`. No separator lines
>    between pills. Contents stay as §3.4 lists (program tiles, badge, label, activity word, close on
>    hover/selected, progress bar along the pill's inside bottom edge, 2 epx, inset 10 epx, radius 1).
> 2. **No separate workspace layer** (overrides §4.1's translucent workspace layer): pane cards sit
>    directly on the window ground (Mica Alt/Acrylic + Aurora), 8 epx from the window edges / sidebar,
>    exactly like Rex Mac screenshots 1–2, where the cards float on the gradient.
>
> Everything else in this spec stands.

> ## Amendments from the visual polish pass (2026-11-18) — measured against Rex Mac (docs/reference 1–3 and the Rex beta captures); they override the values they name
>
> 1. **Rounder, lighter glass.** Pane cards: `CornerRadius` **12**, gaps **8** (8 from the edges, unchanged). Cards are a *lighter* glass than the ground, as on Rex Mac: Mica Alt focused `#F2FFFFFF` / `#EB2B2A31`, inactive `#ADFFFFFF` / `#AD2B2A31` (light / dark); Acrylic `#F2FFFFFF` / `#DB2B2A31` focused (× 0.8–1.0 with Glass opacity) and `#8CFFFFFF` / `#8C2B2A31` inactive (× 0.55–1.0); Solid `#FFFFFFFF` / `#FF2B2A31` on ground `#F0EFF2` / `#1C1C1E`. In dark the hairline is lit from above (white 13 % at the top fading to 5 %). `OverlayCornerRadius` is **12** app-wide (palette, flyouts, menus, dialogs, teaching tips); controls keep 4.
> 2. **Focus without the ring by default.** The focused card is the denser glass, lifted on a 24 epx-blur shadow and edged by a brighter hairline (`RexPaneFocusedStrokeBrush`). The accent outline is opt-in: Settings → Appearance → "Highlight the focused pane's edges", **off** by default, matching Rex Mac's "Highlight active pane edges". The dim covers the whole card evenly (header background included); header text sits above it.
> 3. **Tabs on a track.** The pills sit on one rounded band, `RexTabTrackBrush` (black 5 % / white 7 %), spanning the visible pills, with a 1 × 16 hairline between two unselected pills. Pills: height 32, `CornerRadius` 12, padding 9,0,4,0, 2 epx apart, label 13 epx. The program icons are a **deck**: the focused pane's tile (18 epx) in front, each other program a 14 epx tile peeking out 4 epx to its right, behind it.
> 4. **Program tiles everywhere.** Pane headers show the 16 epx tile (not a bare glyph). Tile radius is 27 % of the edge; glyphs 64 % (Segoe), 70 % (Nerd), 54 % (monogram). Near-white tiles get a dark rim and near-black ones a light rim. Codex is a white tile with a black mark. New keys: fish, php, perl, zig, kotlin, gradle, scala, clojure, haskell, ocaml, elixir, julia, r, dart, nix, files, search, json, disk, media, music, mail, chat (see `Shell/ProgramIcons.cs`); Settings → Appearance → Colourful icons expands into the full sheet.
> 5. **Pane header.** 36 epx (30 in Compact), padding 12,0,6,0, tile 16 + 8 gap + 13 Bold title; buttons 26 × 26, radius 6, 13 epx glyphs, 2 apart.
> 6. **Terminal type and air.** Default font **10 pt** (13.3 epx, just above the 13 epx chrome, like Rex Mac's 12 px under 13 pt chrome), line height 1.25. The card insets its terminal **12** left/right (the text lines up with the header tile), **4** under a docked header or **10** without one, **8** at the bottom. Ctrl+0 resets to 10 pt.
> 7. **Pane headers: On hover** floats the header over the body on `RexFloatingHeaderBrush` (in-app acrylic) after the pointer rests 90 ms in the card's top 36 epx; leaving the band hides it. **Pane style: Compact** tiles the cards into one frame: 1 epx gutters, only the frame's outer corners rounded (12), no strokes or lift, 30 epx headers, and (with the highlight on) a 2 epx accent line on the focused pane's top edge.
> 8. **Narrow windows.** Below **1000** epx (a half-screen snap at 100 %) the sidebar is an overlay, so the panes keep the width (was 860).
> 9. **Acrylic ("Frosted glass").** Thin desktop acrylic; dark tint `#1C1B21` at 0.20, light `#F4F2F7` at 0.12; luminosity opacity from Glass opacity 40–100 %: dark 0.12–0.82, light 0.30–0.92 (70 % default ≈ 0.47 / 0.61), so the blurred desktop reads through the title row, sidebar and gaps while cards stay denser.
> 10. **Motion.** Animations: Snappy (default) / **Natural** (every chrome duration × 1.5) / Off (`Services/Motion.cs`).
> 11. **Taskbar.** `ITaskbarList3` works unpackaged and is used: the button's progress is the current session's most urgent tab progress (error red, paused yellow, else the least advanced percentage, else indeterminate), and the overlay icon is the shell's stock warning icon while a tab out of sight needs you (else its error icon when one failed). Settings → Notifications → "Progress and badge on the taskbar button" (on).
> 12. **First run.** A `TeachingTip` points at the sidebar's Add host button once ("Add your first host", actions Add host / Got it); dismissing it or adding a host retires it (`seen_add_host_tip`), and it steps away while the sidebar is hidden. The sidebar's empty state no longer repeats an Add host button. The first time one of the user's own sessions is on screen, the §9.12 tip points at the identity chip ("Switch sessions here", action "Use Ctrl+K instead"; `seen_switcher_tip`); never in the demo.
> 13. **Settings window.** Navigation glyphs are colour-coded per page (as in Windows 11 Settings and Rex Mac's coloured tiles); the window is titled "Settings – Windex" for the taskbar and Alt+Tab.
> 14. **Warmer aurora middle** (overrides §4.3's 0.50 stop): light `#40EEDCEA`, dark `#24604858`, so the ground runs pink-lilac → warm neutral → butter (light) and mauve → dusky rose → umber (dark) like Rex Mac's, with no blue band.
> 15. **Pane titles are Bold** 13 epx (Rex Mac's SF Pro Semibold reads as Segoe UI Variable Bold).

## 0. Decisions at a glance

| Topic | Decision |
|---|---|
| Feel | Rex Mac's layout, glass and focus behaviour, built from Windows 11 primitives: Mica Alt, TabView, NavigationView, SettingsCard, InfoBar, ContentDialog, CommandBarFlyout, Segoe UI Variable and Segoe Fluent Icons. |
| Backdrop | **Mica Alt** by default; Microsoft recommends it for apps with a tabbed title bar. Settings → Appearance → Window material offers Mica Alt, Mica, **Acrylic ("Frosted glass", the screenshot 3 look)** and Solid. |
| Ground | A window-wide **Aurora** gradient wash (pink-lilac to butter in light, mauve to umber in dark) under a translucent workspace layer. This is the Windows version of Rex Mac's gradient ground. Alternatives: Accent or None. |
| Title bar | A custom 48 epx (Tall) row: sidebar toggle, identity chip (session and host), TabView with connected tabs, + and a new-item menu, drag space, Activity, Search and Settings, then the **system** caption buttons, so Snap Layouts keeps working. |
| Sidebar | **Left** by default; Right is a setting (§6 explains why). It holds hosts → sessions → tabs, with a collapsed Tailnet group of discovered machines. |
| Pane cards | **12 epx radius**, **8 epx gaps**, 8 epx padding inside the workspace, 1 epx hairline, 36 epx header. The focused card is the denser glass with a lift and a brighter hairline (the accent stroke is opt-in); unfocused cards get a more transparent surface and a 25–30% dim. |
| Terminal | Cascadia Mono **10 pt**, line height 1.25 and padding 12 / 4 / 12 / 8, drawn on a **transparent** swap chain with grayscale antialiasing so the glass shows through. Built-in themes: **Rex Dusk** (dark) and **Rex Dawn** (light). |
| Icons | Superlogical's program icon set is **not licensed for reuse**, so we don't ship it. Instead: Segoe Fluent Icons glyphs, Fluent UI System Icons (MIT) paths, a few authored PathIcons, coloured monogram tiles and an InfoBadge status overlay. Users can drop in their own icon packs. |
| Keys | Windows Terminal conventions (Ctrl+Shift+…). **Session switcher defaults to Ctrl+Shift+K**, because Ctrl+K is readline/nano's kill-line; **Ctrl+K is a one-click opt-in**. The command palette is Ctrl+Shift+P. |

---

## 1. Principles

1. **The terminal is the content.** The server owns text, colours and split geometry. Windex only draws chrome around them and never recolours output; a theme reaches the terminal by *declaring* it to the server. **Terminal content never animates.**
2. **Glass is chrome.** Materials, translucency, shadows and motion belong to chrome surfaces only: title bar, sidebar, cards, overlays.
3. **Native first.** If WinUI has a control for the job, use it and restyle it only through resources (lightweight styling). Draw custom visuals only for the terminal, splitters and program tiles.
4. **Quiet when healthy.** A connected host or live pane shows no "Connected" label. Words appear only for trouble ("Reconnecting", "Offline", "Input paused") and for program activity ("Needs input", "Error", "Done").
5. **Safe by default.** Closing a pane, tab or session names what will end and defaults to **Keep running**. Closing the window never stops anything on a server. Attaching never starts or stops a server, and never creates or destroys a session without being asked.
6. **Keyboard first.** Every action has a palette row, every shortcut can be rebound, and keys the app doesn't use reach the program.

---

## 2. Platform baseline

- **Windows App SDK:** at least 1.8. That version added `Microsoft.Windows.Storage.Pickers`; the `TitleBar` control dates from 1.7. Build on the current stable 2.x line (2.7.0 on 2026-11-17) when possible, but the spec uses no 2.x-only API.
- **Packaging:** MSIX with package identity. It is needed for AppNotification activation, the taskbar badge, `StartupTask`, the Jump List and the `windex:` protocol.
- **NuGet:** `Microsoft.WindowsAppSDK`, `Microsoft.Graphics.Win2D`, `CommunityToolkit.WinUI.Controls.SettingsControls` (8.2.x) and `CommunityToolkit.WinUI.Controls.Segmented`.
- **Fonts:**
  - Segoe UI Variable: chrome text (the WinUI default).
  - Segoe Fluent Icons, through `{ThemeResource SymbolThemeFontFamily}`.
  - Cascadia Mono: ships with Windows 11.
  - Symbols Nerd Font Mono 3.x (MIT): bundled, used only as the terminal's fallback for Powerline and Nerd glyphs.
- **OS:** the design targets Windows 11 22H2 or later. On Windows 10, the Mica options are hidden and Solid is the default.
- **Reference apps:** Windows Terminal, File Explorer (Mica Alt with tabs), Files, PowerToys Settings and WinUI 3 Gallery.
  - Windows Terminal is WinUI 2, so treat it as a visual reference only.
  - Dev Home was retired in 2025. Don't copy its patterns.

---

## 3. Window anatomy

### 3.1 Overview

The default window is 1280 × 800 at 100% scale, shown with the sidebar open.

```
x=0 (title row 48 epx; the selected tab opens into the workspace layer below it)          x=1280
+--------------------------------------------------------------------------------------------------+
| [||] [L] rex-snake  v  .-------------------.                                                     |
|        duck . Linux    |[A] Stress-test.. x| [N] Bun ~/sit..  [+][v]  ..drag..  [B][S][G] _ [] X |
|                        |                   |                                                     |
| SIDEBAR 260          .-'                   '-----------------------------------------------------|
|  [L] duck            |  layer: top-left radius 8, 8 epx padding                                  |
|   [A] rex-snake   3  |  .---------------------------------------.  .---------------------------. |
|     [A] Stress-te..  |  | [a] Stress-test Snake [|][=][^][.][x] |  | [n] Node ~/Sites/rex-sn.. | |
|     [N] Bun          |  | > Ran git diff -- demo/snake.mjs      |  | S N A K E   Score: 43     | |
|     [>] fish         |  | (focused: crisp, accent rim, lifted)  |  | (unfocused: dimmed)       | |
|   [>] api-server     |  '---------------------------------------'  |                           | |
|  [S] build-box       |                6 epx gap                    |                           | |
|   Reconnecting...    |  .---------------------------------------.  |                           | |
|                      |  | [g] Git Changes ~/Sites/rex-snake     |  |                           | |
|  v Tailnet  4        |  | (unfocused: dimmed)                   |  |                           | |
| ---------------------|  '---------------------------------------'  '---------------------------' |
| [+ Add host]    [..] |                                                                           |
+--------------------------------------------------------------------------------------------------+
```

Legend: `[||]` sidebar toggle · `[L]` host-kind glyph · `[A] [N] [>]` program tiles · `[B]` Activity (bell) · `[S]` Search/palette · `[G]` Settings · `_ [] X` system caption buttons · `[|][=][^][.][x]` split right, split down, zoom, more, close.

### 3.2 Title row: measurements

The row is one `Grid` (`TitleRow`), **48 epx** tall, in the backdrop region. `AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall`, which makes the system caption buttons 48 tall.

```
| 6 | Toggle 40x36 | 4 | Identity chip 140-240 x 40 | 8 | TabView (*)  ............................. | Actions 3x40 (+2 gaps) | 8 | Caption inset |
                                                     [tab 120-240 x 40][tab]...[+ 32x32][v 28x32][drag >= 48]
y:   toggle and action buttons 6..42 · chip 4..44 · tabs 8..48 (they touch the workspace layer) · + and v 12..44
```

| Column | Width | Content |
|---|---|---|
| 0 | 6 | Padding |
| 1 | 40 | `SidebarToggleButton`. Glyph **DockLeft `E90C`**, or **DockRight `E90D`** when the sidebar is on the right. 16 epx. |
| 2 | 4 | Gap |
| 3 | Auto (140–240) | `IdentityChip` (§3.3) |
| 4 | 8 | Gap |
| 5 | * | `TabView` (§3.4), including its footer drag space |
| 6 | Auto (124) | Activity, Search and Settings buttons: 40 × 36 each, 2 epx apart |
| 7 | 8 | Gap |
| 8 | `RightInset` | Space for the system caption buttons (about 3 × 46 = 138 epx). Bind it to `AppWindow.TitleBar.RightInset / XamlRoot.RasterizationScale`; never hard-code it. |

**Title-bar buttons** use `RexTitleBarButtonStyle` (§15):
- Transparent at rest, `SubtleFillColorSecondaryBrush` on hover, `SubtleFillColorTertiaryBrush` when pressed.
- `CornerRadius` 4 (`ControlCornerRadius`).
- 16 epx glyph in `TextFillColorPrimaryBrush`; `TextFillColorDisabledBrush` while the window is inactive.

**Caption buttons** stay the system's (`ExtendsContentIntoTitleBar = true`, never hidden), so Snap Layouts on hover over Maximize keeps working. When `RequestedTheme` differs from the system theme, set the caption colours on `AppWindow.TitleBar` after every `ActualThemeChanged`:
- `ButtonBackgroundColor`: Transparent.
- `ButtonForegroundColor`: `TextFillColorPrimary`.
- `ButtonHoverBackgroundColor`: `SubtleFillColorSecondary`.
- `ButtonPressedBackgroundColor`: `SubtleFillColorTertiary`.
- `ButtonInactiveForegroundColor`: `TextFillColorDisabled`.

**Drag regions.** The main window doesn't use the WinUI `TitleBar` control, because the TabView needs fine-grained drag regions. Instead:
1. Call `Window.SetTitleBar(TitleRow)`.
2. Mark every interactive rectangle as passthrough with `InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects)`. These are the toggle, identity chip, each tab plus the TabView scroll and add buttons, the new-item menu button and the action buttons. Do **not** include the footer drag space.
3. Rects are in physical pixels: multiply by `RasterizationScale`.
4. Recompute on `SizeChanged` of `TitleRow`, on `TabView.TabItemsChanged`, on chip width changes and on DPI changes.
5. Keep at least **48 epx** of the footer as drag space; the tab strip scrolls before it gives that up.

Double-clicking the drag space maximizes, as the system handles it. The Settings window, which is simpler, does use the WinUI `TitleBar` control (§9.11).

**Responsive rules (window width):**
- **< 1000:** the chip drops its second line (host) and shows only the session.
- **< 860:** a docked sidebar becomes an overlay (§6.6).
- **< 720:** the chip becomes icon-only (40 × 40) with the session name in its tooltip, and Activity and Search fold into a More `E712` button.
- **Minimum window:** 640 × 480 (`OverlappedPresenter.PreferredMinimumWidth/Height`).

### 3.3 Identity chip

A `Button` with `RexIdentityChipStyle`, height 40, `MinWidth` 140, `MaxWidth` 240, padding 8,0,8,0, corner radius 4. It is transparent at rest and uses the subtle fills on hover and press.

Contents, left to right:
1. The host-kind glyph (§6.3): 16 epx, `TextFillColorSecondaryBrush`.
2. An 8 epx gap.
3. A two-line stack:
   - **Session name:** 13 epx SemiBold, `TextFillColorPrimaryBrush`, trimmed with an ellipsis.
   - **Host line:** 11 epx Regular, `TextFillColorSecondaryBrush`, in the form `duck · Linux`. While the host is in trouble it is replaced by the trouble word, such as `Reconnecting…`, in `SystemFillColorCautionBrush`.
4. A 6 epx gap.
5. ChevronDown `E70D`, 10 epx, `TextFillColorTertiaryBrush`.

Clicking the chip opens the session switcher (§9.1) anchored under it (`BottomEdgeAlignedLeft`, 360 wide). The tooltip reads `Switch session (Ctrl+Shift+K)`. The automation name is `Session rex-snake on duck`.

### 3.4 Tab strip (TabView)

A `TabView` holds the **current session's** tabs. Tab items have no content; the workspace below is ours.

**TabView settings:**
- `TabWidthMode="Equal"`, `CanReorderTabs="True"`, `CanDragTabs="True"`, `CanTearOutTabs="True"` (WinAppSDK 1.6+; tearing out opens the tab in a new Windex window).
- `IsAddTabButtonVisible="True"`, `CloseButtonOverlayMode="OnPointerOver"`.
- `TabStripFooter` holds the new-item menu button (28 × 32, ChevronDown `E70D` 12 epx) followed by a `Grid x:Name="TabDragSpace" MinWidth="48"`.

**Tab size:**
- Height 40 (y 8–48). Width 120–240; set `TabViewItemMinWidth` to 120 and keep the default max of 240.
- `CornerRadius="8,8,0,0"` (the top corners use `OverlayCornerRadius`).

**Tab looks**, done with lightweight styling (§15):

| State | Background | Border | Label colour |
|---|---|---|---|
| Selected | `RexWorkspaceLayerBrush`, the same brush as the workspace layer, so the tab is **connected** to it | 1 epx `CardStrokeColorDefaultBrush` on top, left and right; a 1 epx mask in the layer brush hides the layer's top stroke under the tab | `TextFillColorPrimaryBrush` |
| Unselected | Transparent | none | `TextFillColorSecondaryBrush` |
| Hover | `SubtleFillColorSecondaryBrush` | none | `TextFillColorSecondaryBrush` |
| Pressed | `SubtleFillColorTertiaryBrush` | none | `TextFillColorSecondaryBrush` |

A 1 × 16 `DividerStrokeColorDefaultBrush` separator sits between adjacent unselected tabs (the TabView default).

**Tab header contents,** left to right (padding 12,0,4,0):
1. **Program icon stack** (§8): up to N tiles, 16 × 16. Default N = 3 (setting 0–5), focused pane's program first, overlapping by 6 epx, each with a 1.5 epx ring in the tab's background colour.
2. **Status badge:** a 10 epx InfoBadge on the first tile's bottom-right corner (§8.5).
3. An 8 epx gap, then the **label**: 12 epx Regular, `CharacterEllipsis`, one line. A tab still named "Shell" shows its focused pane's title, as Rex does.
4. A 6 epx gap, then the **activity word**: 12 epx SemiBold, never trimmed (the label trims first).
   - `Needs input`, `Needs approval`, `Question`, `Needs sign-in`, `Bell` and `Paused` use `SystemFillColorCautionBrush`.
   - `Error` uses `SystemFillColorCriticalBrush`.
   - Unseen `Done` uses `SystemFillColorSuccessBrush`.
   - Working and idle tabs show **no word**.
   - A muted tab shows Mute `E74F` (12 epx, tertiary) in place of the word.
5. **Close button:** the TabView built-in, 32 × 24, Cancel `E711` at 10 epx, shown on hover or selection. It opens the close confirmation (§9.6).
6. **Progress** (OSC 9;4): a WinUI `ProgressBar` along the inside bottom of the tab: `Height` 3, `Margin` 12,0,12,3, `VerticalAlignment` Bottom.
   - It shows `Value` when a percentage is known, and `IsIndeterminate` otherwise.
   - `ShowPaused` turns it caution-coloured and `ShowError` turns it critical; this is built into ProgressBar.
   - There is no word for progress except `Paused` and `Error`.

**Tab interactions:**
- Tooltip: `{label}` on the first line, then `{session} · {host}`, then the program's activity message when there is one.
- Middle-click closes (with confirmation).
- Double-click the label to rename inline (§9.5). F2 also renames while the tab has keyboard focus.
- Right-click opens the tab menu (§9.7).

**Add button:** the built-in `+` (Add `E710`), 32 × 32, tooltip `New tab (Ctrl+Shift+T)`. The **new-item menu** (`v`) is a `MenuFlyout`: New tab (Ctrl+Shift+T) · New session… (Ctrl+Shift+N) | Split right (Ctrl+Shift+D) · Split down (Ctrl+Shift+E) | Add host… (`|` = separator).

### 3.5 Actions cluster

| Button | Glyph | Tooltip | Opens |
|---|---|---|---|
| Activity | Ringer `EA8F`, with an `InfoBadge` at the top-right (offset −4,4) | `Activity (Ctrl+Shift+A)` | Activity flyout (§9.8). The badge is a **value** badge: `CautionValueInfoBadgeStyle` with the number of out-of-sight tabs that need you, else `CriticalValueInfoBadgeStyle` with the number in error. There is no badge when neither applies. |
| Search | Search `E721` | `Search sessions and commands (Ctrl+Shift+K · Ctrl+Shift+P)` | Palette (§9.1) |
| Settings | Settings `E713` | `Settings (Ctrl+,)` | Settings window (§9.11) |

A **Downloads** button (Download `E896`) appears to the left of Activity only while a transfer is running, or for 30 s after one finishes (§9.9).

### 3.6 Window title, taskbar, full screen

- **`AppWindow.Title`:** `{tab} – {session} · {host} – Windex`, or `Windex – {host}` before a session is chosen.
- **Taskbar:**
  - `ITaskbarList3.SetProgressState/SetProgressValue` mirrors the most urgent progress in the window's session.
  - `SetOverlayIcon` shows a caution glyph while any tab needs you.
  - `FlashWindowEx(FLASHW_TRAY | FLASHW_TIMERNOFG)` flashes the button when a tab needs input and the window is inactive.
  - With package identity, `BadgeUpdateManager.CreateBadgeUpdaterForApplication()` shows the total number of tabs that need you.
- **Jump List** (`Windows.UI.StartScreen.JumpList`): up to 8 recent sessions (`rex-snake · duck`), which activate through `windex://open?host=…&session=…`.
- **Full screen** (F11 or Alt+Enter): `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)`.
  - The title row hides. Resting the pointer in the top 6 epx for 300 ms slides it in as an overlay: `AcrylicInAppFillColorDefaultBrush` with `ThemeShadow` at Z 32.
  - Pane cards keep their 8 epx padding.
- **Maximized and snapped** windows have square corners (system behaviour). The title row stays 48.

---

## 4. Materials

### 4.1 Layer stack (bottom to top)

1. **System backdrop** (`Window.SystemBackdrop`), covering the whole window.
2. **Aurora wash:** a `Rectangle x:Name="Aurora"` across the whole window, `IsHitTestVisible="False"`, filled with `RexAuroraBrush` (§4.3).
3. **Workspace layer:** a `Border` covering everything below the title row and right of a docked sidebar.
   - Background `RexWorkspaceLayerBrush`.
   - `BorderBrush` `CardStrokeColorDefaultBrush`, `BorderThickness="1,1,0,0"`.
   - `CornerRadius="8,0,0,0"` when the sidebar is docked on the left, `0,8,0,0` on the right, and `0` with no sidebar.
   - The selected tab uses the same brush over the same aurora, so the two read as one surface.
4. **Content:** title-row controls, sidebar rows and pane cards.
5. **Overlays:** palette, find bar, in-app notices and drag-and-drop targets, using `AcrylicInAppFillColorDefaultBrush` with `ThemeShadow` at Z 32.

### 4.2 Window material setting

Settings → Appearance → **Window material**. The default is Mica Alt. The four modes:

| Mode | `SystemBackdrop` | Workspace layer (L / D) | Focused card (L / D) | Inactive card (L / D) |
|---|---|---|---|---|
| **Mica Alt** (default) | `new MicaBackdrop { Kind = MicaKind.BaseAlt }` | `#73FFFFFF` / `#4D2C2C2C` | `#F2FFFFFF` / `#E61F1E24` | `#A6FFFFFF` / `#A61F1E24` |
| Mica | `new MicaBackdrop { Kind = MicaKind.Base }` | same | same | same |
| **Acrylic ("Frosted glass")** | `RexAcrylicBackdrop` (§4.4) | `#26FFFFFF` / `#1A2C2C2C` | `#D9FFFFFF` / `#C71F1E24` | `#8CFFFFFF` / `#801F1E24` |
| Solid | `null`; the root `Background` is `SolidBackgroundFillColorBaseBrush` (`#F3F3F3` / `#202020`) | `LayerFillColorDefaultBrush` (`#80FFFFFF` / `#4C3A3A3A`) | `#FFFFFFFF` / `#FF1F1E24` | `#FFF7F7F9` / `#FF1B1A20` |

**Fallbacks:**
- **Transparency effects off** (`UISettings.AdvancedEffectsEnabled == false`, watched through `AdvancedEffectsEnabledChanged`), **Energy saver** and **High contrast** all use the **Solid** column. The system already drops the backdrop in those cases; we make the cards opaque too so terminal text stays legible.
- Mica and Mica Alt fall back to solid while the window is inactive; this is native behaviour and we keep it.

### 4.3 Aurora (the gradient ground)

`RexAuroraBrush` is a `LinearGradientBrush` with `StartPoint="0,0"` and `EndPoint="1,1"`, mapped relative to the window. It reproduces Rex Mac's ground: pink-lilac at the top-left fading to butter at the bottom-right in light mode, and mauve to umber in dark mode.

| Stop | Light | Dark |
|---|---|---|
| 0.00 | `#8CF5C6EA` | `#4D7B4F8F` |
| 0.50 | `#40D8DBF8` | `#1F404A7A` |
| 1.00 | `#8CFFE4A0` | `#40A06A3A` |

Settings → Appearance → **Workspace tint** offers:
- **Aurora** (default).
- **Accent:** stops `SystemAccentColorLight2` at 35%, transparent, then `SystemAccentColorDark1` at 25%.
- **None:** the Rectangle is collapsed.

The tint is strongest in the title row and sidebar, where it sits straight on the backdrop. It is softer in the workspace and softest behind cards. This matches Windows layering (base, then layer, then card) and keeps the Mac feel.

### 4.4 Frosted glass over the desktop (screenshot 3)

Mica samples only the wallpaper and is nearly opaque. The see-through, blurred-desktop look needs **desktop Acrylic**, the only system material that blurs the windows and desktop actually behind the window.

**`RexAcrylicBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop`** wraps a `DesktopAcrylicController` (the documented custom-backdrop pattern):
- In `OnTargetConnected(target, xamlRoot)`, call `controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot))` and then `controller.AddSystemBackdropTarget(target)`.
- `Kind = DesktopAcrylicKind.Thin`.
- **Dark:** `TintColor #1C1B21`, `TintOpacity 0.30`, `LuminosityOpacity 0.62`, `FallbackColor #202020`.
- **Light:** `TintColor #F4F2F7`, `TintOpacity 0.20`, `LuminosityOpacity 0.72`, `FallbackColor #F3F3F3`.
- Customised controllers stop following the theme, so reapply these values on `ActualThemeChanged`.
- **Glass opacity** (Settings, Acrylic only, 40–100%, default 70%) maps linearly onto two values: `LuminosityOpacity` from 0.40 to 0.95, and the card alpha from 0.55× to 1.0× of the table values.
- **Keep glass when the window is inactive** (off by default) pins `SystemBackdropConfiguration.IsInputActive = true`.

For the glass to show inside a pane, the **terminal's default background is not painted**: the swap chain is transparent (§7.1). The terminal then sits on the translucent card, which sits on the blurred desktop. An explicit background a program sets is still painted.

### 4.5 Theme and accent

- **App theme** (Use system setting / Light / Dark) sets `FrameworkElement.RequestedTheme` on each window's root.
- The accent is the **Windows accent** by default, through `AccentFillColorDefaultBrush` and friends. Settings can override it with one of 8 swatches by replacing `SystemAccentColor` and its `Light1–3`/`Dark1–3` variants in app resources.
- The accent is used for:
  - the focused-card stroke,
  - selection indicators,
  - primary buttons,
  - progress bars,
  - the cursor (optional, §7.3),
  - search matches.

  It is never used for large fills.

---

## 5. Pane cards

### 5.1 Geometry

- **Card:** a `Border` with `RexPaneCardStyle`:
  - `CornerRadius` 8 (`OverlayCornerRadius`, matching the Windows 11 window corner).
  - `BorderThickness` 1.
  - **Gap** between cards: **6**.
  - **Padding** between the workspace layer's edge and the cards: **8** on all sides.
- **Minimum card:** 160 × 72 epx. Split ratios stay within 0.1–0.9 (the Rex rule).
- **Layout:** `PaneLayoutPanel : Panel` measures cards from the server's split tree; the client never invents a layout. The CommunityToolkit `GridSplitter` is **not** used, because the layout is a tree rather than a grid.

| Part | Focused | Unfocused |
|---|---|---|
| Fill | `RexPaneCardBrush` | `RexPaneCardInactiveBrush` |
| Stroke | 1 epx `RexPaneFocusStrokeBrush` (accent: 80% in light, 70% in dark) | 1 epx `RexPaneStrokeBrush` (`#0F000000` / `#1AFFFFFF`) |
| Elevation | `Translation="0,0,8"` with `ThemeShadow` | `Translation="0,0,0"` |
| Content dim | none | `RexPaneDimBrush` overlay over the terminal body: `#4DFAF9FC` in light (a 30% wash), `#47000000` in dark (28%) |
| Header text | `TextFillColorPrimaryBrush` | `TextFillColorSecondaryBrush` |
| Header buttons | visible at rest | hidden until the pointer enters the card |

**How it is built:**
- Fill changes use `Border.BackgroundTransition = new BrushTransition { Duration = 167 ms }`.
- The dim is a `Rectangle` in the body row (`IsHitTestVisible="False"`), clipped to the card's bottom corners, with `OpacityTransition` of 167 ms.
- The stroke changes instantly.

**Settings → Appearance:**
- **Dim inactive panes:** Off / Surface / Content / Surface and content (the default, as on Rex Mac).
- **Highlight focused pane:** on by default.

**Special cases:**
- **Window inactive:** the focused card's stroke falls back to `RexPaneStrokeBrush`, its shadow stays, and every cursor draws hollow. Unfocused cards are unchanged.
- **Single pane:** never shows the accent stroke or the dim, because there is nothing to tell apart.

### 5.2 Header

The header is **36** tall and is the card's first row (`Grid.Row=0`). There is no divider between the header and the terminal.

```
|12| icon 16 |8| Title (13 Bold, trims) |8| [Zoomed] |6| Activity word |  ...  | progress 48x3 |8| [SplitR][SplitD][Zoom][More][Close] |4|
                                                                              buttons 28x28, 2 epx apart, 14 epx glyphs
```

- **Program icon:** glyph mode (§8.3), 16 epx. It uses the program's hue on the focused card when Colorful icons is Always, and `TextFillColorSecondaryBrush` otherwise.
- **Title:** the pane name, or its program-set title while a "shell"-named pane runs a program. Double-click renames inline (§9.5).
- **Zoomed chip:** shown while zoomed. A `Border` with corner radius 4, padding 6,1, `SubtleFillColorSecondaryBrush` and the text "Zoomed" at 11 epx Secondary.
- **Activity word:** the pane's own trouble or progress word, from the tab vocabulary; Done and Bell stay with the tab. The program's message is its tooltip.
- **Progress:** an inline `ProgressBar`, 48 × 3, with the same states as on tabs.
- **Exception strip** (replaces the word, §9.3): `Input paused · Reconnecting…` in caution colour, followed by a `HyperlinkButton` "Retry" at 12 epx.

**Header buttons** (`RexPaneHeaderButtonStyle`: 28 × 28, corner radius 4, transparent; hover `SubtleFillColorSecondaryBrush`; pressed `SubtleFillColorTertiaryBrush`; glyph `TextFillColorSecondaryBrush`, Primary on hover):

| Button | Icon | Tooltip | Unavailable when |
|---|---|---|---|
| Split right | PathIcon `RexSplitRightPath` (§15) | `Split right (Ctrl+Shift+D)` | — |
| Split down | PathIcon `RexSplitDownPath` | `Split down (Ctrl+Shift+E)` | — |
| Zoom / Restore | FullScreen `E740` / BackToWindow `E73F` | `Zoom pane (Ctrl+Shift+Enter)` / `Restore` | The only pane in its tab (35% opacity; the tooltip gives the reason) |
| More | More `E712` | `More options` | — |
| Close | Cancel `E711`; turns `SystemFillColorCriticalBrush` on hover | `Close pane (Ctrl+Shift+W)` | — |

More opens the pane menu (§9.7), anchored with `BottomEdgeAlignedRight`.

**Button behaviour:**
- They act on their own pane and never take focus.
- Each has `AutomationProperties.Name` and `AcceleratorKey` set.
- **Reveal:** on the focused card they are visible at rest at `Opacity` 0.75, and 1 on hover. On unfocused cards they are at `Opacity` 0 and not hit-testable until the pointer enters the card, then fade in over 83 ms.
- **Narrow headers:** below 220 epx of card width, only Zoom and Close remain. Below 160, only Close.

**Pane headers** setting (Appearance):
- **Always** (default).
- **On hover:** no row; resting the pointer 90 ms in the top 36 epx floats the header over the terminal on `AcrylicInAppFillColorDefaultBrush`. It never moves the terminal.
- **Hidden.**

### 5.3 Splitters

`PaneSplitter : Grid` is a subclass so it can set `ProtectedCursor`. There is one per split boundary.

- **Visible width:** none; the 6 epx gap itself shows the ground. **Hit area:** 10 epx, centred on the gap and overlapping each card edge by 2 epx.
- **Cursor:** `InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast)` for vertical boundaries and `SizeNorthSouth` for horizontal ones.
- **Hover** (after 150 ms): a 2 × 32 grip with radius 1, `ControlStrongFillColorDefaultBrush`, centred and fading in over 83 ms.
- **Dragging:** the grip becomes a full-length 2 epx line in `AccentFillColorDefaultBrush`. Cards re-layout live, and terminals re-render at the new size each frame with **no interpolation**. The ratio, clamped to 0.1–0.9, is sent to the server on release.
- **Double-click** equalizes (ratio 0.5).
- **Keyboard:** Alt+Shift+Arrow resizes the focused pane in 5% steps.

### 5.4 Zoom

- Zoom (Ctrl+Shift+Enter) makes the pane fill the pane area (8 epx padding kept).
- Sibling cards become `Visibility.Collapsed` but stay loaded; their swap chains are not destroyed.
- The header shows the **Zoomed** chip, and the zoom button becomes Restore.
- The change is **instant**: no scale or connected animation.

### 5.5 Single-pane tabs

The card stays: same 8 epx padding, radius and fill, so the glass look is consistent. **The header stays too** (default Always), because:
- Split right and Split down are reachable before any split exists.
- The pane title can differ from the tab label.

It has no accent stroke and no dim. With Pane headers set to On hover or Hidden, the single pane shows no header.

### 5.6 Compact density (option)

Appearance → **Pane style** (a `Segmented` control: Comfortable · Compact; default Comfortable). Compact means:
- Cards are 0 radius with no stroke, shadow or padding, separated by 1 epx `DividerStrokeColorDefaultBrush` lines.
- No headers unless Pane headers is Always.
- The focused pane gets a 2 epx accent line along its top edge.

### 5.7 Find bar

Ctrl+Shift+F opens the find bar, floating at the top-right of the terminal body, 8 epx from the edges.
- **Surface:** 320 × 40 `Border`, corner radius 8, `AcrylicInAppFillColorDefaultBrush`, 1 epx `SurfaceStrokeColorFlyoutBrush`, `ThemeShadow` at Z 32.
- **Contents, left to right:**
  1. Search `E721` at 14 epx.
  2. A borderless `TextBox` with placeholder `Find`.
  3. The count: `2/5` in 12 epx Secondary, or `No results` in critical.
  4. A 1 × 16 divider.
  5. Previous (ChevronUp `E70E`), Next (ChevronDown `E70D`) and Close (Cancel `E711`), each 28 × 28.
- **Keys:** Enter or F3 for next, Shift+Enter or Shift+F3 for previous, Esc to close.
- **Matches** (§7.5) never dim the terminal.
- **Staying clear of the match:** if the current match is under the bar, the bar moves to the bottom-right corner over 167 ms.

---

## 6. Sidebar

### 6.1 Position: left by default

Rex Mac docks its hosts list on the right. On Windows, **left** is correct, for four reasons:
1. **Windows navigation lives on the left:** NavigationView Left, Settings, File Explorer, Files, Outlook, Teams, Dev tools. A right-hand panel reads as an inspector or details pane on Windows.
2. **Spatial link.** The toggle and identity chip sit at the top-left of the title row, and the sidebar is the expanded form of the chip, opening directly beneath it. The WinUI `TitleBar` also puts its pane toggle at the far left.
3. **The right edge is already crowded:** actions, caption buttons, Snap Layouts and the scrollbar overlay.
4. **The connected tab strip** begins near the layer's rounded top-left corner, giving one continuous L-shaped frame.

**Settings → Appearance → Sidebar position: Left / Right** is there for Mac muscle memory. In Right mode, the sidebar sits right of the workspace and its toggle uses DockRight `E90D`. The toggle itself stays at the top-left.

### 6.2 Structure

- **Width:** 260 by default; 200–360, adjustable by dragging the 6 epx resize handle on its inner edge (`SizeWestEast` cursor). Double-click the handle to reset.
- **Background:** transparent (backdrop plus aurora).
- **Content:** a `TreeView` (`SelectionMode="Single"`, `CanDragItems="False"`) with an `ItemTemplateSelector` for HostItem, SessionItem, TabItem, TailnetGroupItem and DiscoveredMachineItem. Top padding 8, sides 8.
- **Selection:** one item is selected. It is the current **tab** if its session is expanded, otherwise the current **session**.
  - The selected row: `SubtleFillColorSecondaryBrush`, radius 4, with a 3 × 16 pill in `AccentFillColorDefaultBrush` (radius 1.5) on its left edge, like NavigationView.
  - Keyboard focus adds the system focus visual (2 epx).

| Row | Height | Indent | Contents |
|---|---|---|---|
| Host | 32 | 0 | Kind glyph 16 (Secondary) · 10 · name 13 SemiBold · trailing status (§6.4) · hover More `E712` 24 × 24 |
| Session | 32 | 16 | Program tile 20 (current tab's program) with its status badge · 10 · name 13 Regular · trailing activity word (11 SemiBold, the most pressing word among its out-of-sight tabs) · tab count (11, Tertiary, only above 1) |
| Tab | 28 | 32 | Program tile 16 · 8 · label 12 · trailing activity word 11 SemiBold or Mute glyph |
| Tailnet group | 28 | 0 | Network `E968` 16 · "Tailnet" 12 SemiBold Secondary · count 11 Tertiary · chevron; collapsed by default |
| Discovered machine | 28 | 16 | Kind glyph 16 · machine name 12 · on hover, an "Add" `HyperlinkButton` (12) |

**Session expansion:** the current session is expanded (its tabs listed) and others are collapsed. Expanding a session that hasn't been opened shows no tabs until it is opened; nothing is attached just by expanding.

**Footer:** 44 tall, with a 1 epx `DividerStrokeColorDefaultBrush` line on top.
- Left: a subtle `Button` with Add `E710` and "Add host".
- Right: a 32 × 32 More `E712` button with a menu: Refresh hosts · Collapse all · Manage hosts… (opens Settings → Remote hosts).

**Empty state** (no saved hosts): centred in the sidebar.
1. Network `E968` at 32 epx, Tertiary.
2. "No hosts yet" (14 SemiBold).
3. "Add a Rex server on your tailnet or over SSH." (12 Secondary).
4. An accent `Button` "Add host".

**"This PC"** appears as a host only when a local endpoint answers, such as a Rex server in WSL on `http://127.0.0.1:7297`. It has no remove action.

### 6.3 Host kinds (chosen in Add host / Edit host)

| Kind | Icon | Source |
|---|---|---|
| This PC (local) | PC1 `E977` | Segoe Fluent Icons |
| Desktop | TVMonitor `E7F4` | Segoe Fluent Icons |
| Laptop | DeviceLaptopNoPic `E7F8` | Segoe Fluent Icons |
| Server | MediaStorageTower `E965` | Segoe Fluent Icons |
| Cloud VM | Cloud `E753` | Segoe Fluent Icons |
| Web endpoint (HTTPS) | Globe `E774` | Segoe Fluent Icons |
| Linux box | PathIcon `RexPenguinPath` (authored, generic penguin) | §15 |
| Mac / mini PC | PathIcon `RexMiniBoxPath` (authored, generic small box) | §15 |

The default is chosen from discovery: the OS from Tailscale picks Linux or Mac, an `https://` endpoint gets Globe, and everything else gets Server.

### 6.4 Host status words (trouble only)

| State | Trailing content | Colour |
|---|---|---|
| Connected | nothing | — |
| First connect | `ProgressRing` 14 (`IsActive`) | Accent |
| Reconnecting | ProgressRing 14 and "Reconnecting" (12) | `SystemFillColorCautionBrush` |
| Offline / unreachable | "Offline", plus a "Retry" HyperlinkButton on hover | `TextFillColorTertiaryBrush` |
| Authentication / SSH failure | "Can't connect" | `SystemFillColorCriticalBrush`; clicking shows the InfoBar (§9.3) |
| Server too old | "Update Rex" | Caution |

### 6.5 Context menus (`MenuFlyout`)

Items carry Segoe glyphs; `|` marks a separator.

- **Host:** Connect `E703` / Disconnect (client-only; programs keep running) · Reconnect `E72C` · New session on {host}… `E710` | Edit host… `E70F` · Copy address `E8C8` · Open in Windows Terminal `E756` (SSH hosts only; runs `wt.exe ssh user@host`) | Remove host… `E74D` (confirms first; never touches the server).
- **Session:** Open · New tab `E710` · Rename session… `E8AC` (inline) | Close session… `E711` (the confirmation names the host and the number of tabs and programs).
- **Tab row:** the same menu as the tab strip (§9.7).

### 6.6 Overlay mode and motion

- **Below 860 epx** of window width, or when the user unpins it, the sidebar opens as an overlay over the workspace, as NavigationView does in Minimal mode:
  - `AcrylicInAppFillColorDefaultBrush`, `ThemeShadow` at Z 32.
  - Light-dismissed; panes do not resize.
- **Docked open:** the pane area takes its final width **at once**, so terminals resize exactly once. Only the sidebar's own visual moves: `Translation.X` from −16 to 0 plus Opacity from 0 to 1, over 250 ms with the decelerate curve.
- **Close:** Opacity to 0 and X to −16 over 167 ms. Then the column collapses, and the panes resize once.

---

## 7. Terminal surface

### 7.1 Rendering

- **Swap chain:** one `CanvasSwapChainPanel` per pane, with `CanvasSwapChain(device, w, h, dpi, B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Premultiplied)`. Each frame clears to **Transparent**.
- **Painted cells:** a background a program sets explicitly, and inverse video, are painted. The theme's default background is not, so the card and material show through.
- **Rendering on demand:** the panel renders only when a frame is dirty: output, cursor blink, selection, resize or DPI change. Size it with `CompositionScaleX/Y` and handle `CompositionScaleChanged`.
- **Antialiasing:** `CanvasTextAntialiasing.Grayscale` by default. ClearType can't be used on a transparent target.
  - Setting **Text antialiasing: ClearType** makes the body paint the theme background opaquely and switches to `ClearType`. It is available in any material and recommended for Solid.
- **Text:** cell-grid glyph runs with ligatures and contextual alternates off (`CanvasTypography`, features `liga` and `calt` set to 0).
- **Box drawing and blocks** (U+2500–259F) and Powerline separators are drawn as geometry snapped to device pixels, not as font glyphs.
- **Emoji:** `CanvasDrawTextOptions.EnableColorFont`.
- **Never animate:** no `Transition`, implicit animation, `RenderTransform`, `Scale` or X/Y `Translation` on the `CanvasSwapChainPanel` or any ancestor up to the card. The card's Z translation (shadow depth) is allowed, because it never moves pixels.

### 7.2 Font

- **Primary:** `Cascadia Mono`, **11 pt** (14.67 epx, where DIP = pt × 96 / 72). Settings range: 6–36 pt.
- **Fallback chain:** user font → `Cascadia Mono NF` / `Cascadia Code NF` (if installed) → bundled `Symbols Nerd Font Mono` → `Segoe UI Emoji` → `Segoe UI Symbol` → DirectWrite system fallback. If Cascadia Mono is missing (Windows 10), `Consolas` takes its place.
- **Cell width:** the font's monospace advance, plus optional letter spacing.
- **Cell height:** font size × **line height 1.25**, rounded up to whole device pixels (19 px at 100% for 11 pt). Settings range: 1.0–2.0.
- **Font picker** (Settings → Terminal): a `Button` "Choose…" opens a `Flyout` (360 × 420) containing:
  - an `AutoSuggestBox` with placeholder `Search fonts`;
  - a `Segmented` control: Monospaced · All fonts;
  - a `ListView` of installed families (DirectWrite `IsMonospacedFont` for the Monospaced filter), each row rendered in its own face.

### 7.3 Padding, cursor and selection

- **Padding** inside the card body: horizontal **10**, vertical **6**. With a header, the top padding is 2, since the header already gives air. Settings range: 0–32 each.
  - Leftover pixels from rounding to whole cells go to the right and bottom.
  - **Fill padding with program colours** (Automatic, the default): when the edge row or column has a non-default background, it extends into the padding.
- **Cursor:**
  - Default shape is a **filled block**. Options: Bar (2 epx), Underline (2 epx) and Hollow block. Programs' DECSCUSR requests win unless **Force cursor shape** is on.
  - **Blink** follows the program's request, at the system caret rate (`GetCaretBlinkTime`), as a hard on/off with no fade. Setting: Follow program / Always steady.
  - Unfocused panes and inactive windows draw a **hollow** 1 epx block at 60% alpha.
  - **Cursor uses accent colour** (on by default) declares the cursor as `SystemAccentColorLight2` (dark) or `SystemAccentColorDark1` (light).
- **Selection:**
  - Uses the theme's selection colour, with cell foregrounds kept.
  - Rounded 3 epx outer corners are on by default (Rex Mac parity).
  - **Copy on select** is off by default.
  - Ctrl+C copies **only while there is a selection**; otherwise it sends ^C (Windows Terminal behaviour).

### 7.4 Scrollbar

A `Microsoft.UI.Xaml.Controls.Primitives.ScrollBar` (vertical) overlaid inside the card's right edge, 2 epx from the border and above the dim layer, bound to scrollback.

| State | Look |
|---|---|
| At rest | The 2 epx panning indicator (`IndicatorMode = TouchIndicator`), shown for 1 s after scrolling |
| Pointer within 16 epx of the edge | The full Fluent scrollbar (`MouseIndicator`) |
| No scrollback | Hidden |

### 7.5 Links and search marks

- **Links:** holding Ctrl over a URL or OSC 8 link underlines it in its own colour, switches to the `Hand` cursor and shows a tooltip with the target.
  - Ctrl+click opens `http`, `https` and `mailto` links with `Launcher.LaunchUriAsync`.
  - Any other scheme asks first with a ContentDialog; the default is Cancel.
- **Search matches:**
  - Every match: an accent 18% fill under a 1 epx accent outline at 70%, with radius 2.
  - The current match: accent at 80%, with its text in `TextOnAccentFillColorPrimary`.

### 7.6 Default terminal themes

Both themes harmonise with the chrome: a warm neutral ground with a hint of violet, and Fluent-blue accents. Each is declared to the server as a full theme.

**Rex Dusk** (dark, default when the app theme is dark):

| Slot | Normal | Bright |
|---|---|---|
| Black | `#2A2830` | `#6B6774` |
| Red | `#FF7B7B` | `#FFA3A0` |
| Green | `#5FD38D` | `#86E3A8` |
| Yellow | `#E8C266` | `#F4D88E` |
| Blue | `#6CB0FF` | `#97C8FF` |
| Magenta | `#C79BFF` | `#DDBFFF` |
| Cyan | `#4FD1D9` | `#7FE3E8` |
| White | `#C9C5D0` | `#F7F5FA` |

Foreground `#E7E4EC` · background `#1B1A1F` (not painted by default) · cursor `#8AB4FF` · cursor text `#1B1A1F` · selection background `#3A4B70` · selection foreground: unchanged.

**Rex Dawn** (light, default when the app theme is light):

| Slot | Normal | Bright |
|---|---|---|
| Black | `#2E2B36` | `#6E6A78` |
| Red | `#C4314B` | `#A51D2D` |
| Green | `#1A7F37` | `#116329` |
| Yellow | `#9A6700` | `#7D4E00` |
| Blue | `#0A63C9` | `#2F81F7` |
| Magenta | `#8250DF` | `#6639BA` |
| Cyan | `#137C86` | `#0F5F67` |
| White | `#6E7781` | `#8C959F` |

Foreground `#24212B` · background `#FBFAFC` (not painted by default) · cursor `#0067C0` · cursor text `#FFFFFF` · selection background `#C9DDFB` · selection foreground: unchanged.

**Contrast:** both foregrounds are above 13:1 against their backgrounds. Every chromatic normal colour meets 4.5:1 against its theme background (Dawn's lowest, yellow, is 4.7:1). Dawn's bright blue is 3.6:1, which is acceptable for a bright slot.

**Theme choice:** Settings → Terminal → Colours has a dark theme and a light theme, each a `ComboBox` with Rex Dusk/Dawn and the built-ins. The active one follows the app theme.

**Import theme…** opens a `FileOpenPicker` (§9.9) for Ghostty theme files, `.itermcolors` and Windows Terminal `schemes` JSON.

---

## 8. Program icons and status badges

### 8.1 Licensing verdict

- **Rex Rune's code** is MIT. We may port its *mapping logic* (process-name aliases, script paths, interpreter fallback) with attribution in Third-party notices.
- **Its icon assets** (`resources/program-icons/tiles` and `glyphs`) are **Superlogical's artwork, and the logos they draw are third-party trademarks.** Their NOTICE says they are *not* covered by the MIT licence and asks reusers to get Superlogical's terms first.
  - **Verdict: we may not ship them** without written permission from Superlogical. The same applies to the Rex app icon in `resources/icons`.
  - If permission arrives later, the user icon-pack mechanism (§8.6) can load them unchanged.
- **Our approach**, all brand-agnostic and clearly licensed:
  1. **Segoe Fluent Icons:** a system font on Windows 11, referenced and not redistributed.
  2. **Fluent UI System Icons** (`microsoft/fluentui-system-icons`, **MIT**). Path data from the pinned *Regular 20* SVGs is converted at build time into `Geometry` resources in `Themes/ProgramIcons.xaml`. Used: **Sparkle**, **Bot**, **Branch Fork**, **Database**, **Hexagon**.
  3. **Authored PathIcons** (§15): split right, split down, penguin, mini box.
  4. **Monogram tiles:** two letters on a hue tile, for named programs with no good category glyph.
  5. **Hues** chosen by us. A colour is not a logo.

### 8.2 Tile anatomy

`ProgramIcon` is a templated control with properties `Key`, `Size` (16 or 20) and `Mode` (`Tile` or `Glyph`).

- **Tile mode** (tabs, sidebar, palette, activity list, toasts):
  - A `Border` with corner radius 4 (at 16) or 5 (at 20).
  - Background: a `LinearGradientBrush` from top (the hue lightened 12% in HSL) to bottom (the hue).
  - A **glass sheen**: a top-half overlay from `#38FFFFFF` to `#00FFFFFF`, plus a 1 epx inner rim of `#26FFFFFF`. Together they give the frosted chip look of Rex Mac's tiles.
  - The glyph is centred, 11 epx (at 16) or 13 epx (at 20), in the tile's `Fg` colour.
  - A **monogram** is Segoe UI Variable Display SemiBold, 8.5 epx (at 16) or 11 epx (at 20), with tracking −20.
- **Glyph mode** (pane headers): the glyph alone at 16 epx, in Secondary (or the hue on the focused card). A monogram program draws its letters in its hue, with no tile.
- **Glyph sources:** a `FontIcon` with `FontFamily="{ThemeResource SymbolThemeFontFamily}"`, a `PathIcon` for geometry, or a `TextBlock` for monograms.
- **Toasts** use a PNG of the tile (48 px, rendered once per key with `RenderTargetBitmap` and cached in `ApplicationData.Current.LocalCacheFolder`), passed to `AppNotificationBuilder.SetAppLogoOverride`.

### 8.3 Mapping

**Resolution order** (from Rex):
1. The status-report app name.
2. The foreground process name. Lower-case it, then strip the path, `.exe`, `.cmd`, `.bat` and `.ps1`, and any version suffix such as `python3.12`.
3. A known script path.
4. The interpreter.
5. **Shell.**

FS means Fluent UI System Icons (MIT). MG means monogram.

| Key | Programs | Icon | Hue | Fg |
|---|---|---|---|---|
| shell | sh, bash, zsh, fish, dash, ksh, tcsh, nu, xonsh, elvish | CommandPrompt `E756` | `#2EA043` | white |
| pwsh | pwsh, powershell | CommandPrompt `E756` | `#2F6BD8` | white |
| cmd | cmd | CommandPrompt `E756` | `#5F6368` | white |
| wsl | wsl, wslhost | PathIcon penguin | `#E95420` | white |
| node | node, npm, npx, tsx, ts-node, nodemon, pnpm, yarn, vite | FS Hexagon | `#5FA04E` | white |
| bun | bun, bunx | MG "Bn" | `#F6DECE` | `#3B2A20` |
| deno | deno | MG "De" | `#1F1F1F` | white |
| python | python, ipython, pip, pytest, uv, uvx, poetry, jupyter | MG "Py" (gradient `#3776AB` to `#FFD43B`) | `#3776AB` | white |
| ruby | ruby, irb, bundle, rails, rake, gem | MG "Rb" | `#CC342D` | white |
| go | go, gopls | MG "Go" | `#00ADD8` | white |
| rust | cargo, rustc, rustup, rust-analyzer | MG "Rs" | `#B7410E` | white |
| dotnet | dotnet, msbuild | MG ".N" | `#512BD4` | white |
| java | java, gradle, gradlew, mvn, kotlin | MG "Jv" | `#E76F00` | white |
| build | make, cmake, ninja, gcc, g++, clang, cl, bazel, just | Repair `E90F` | `#5A6B8C` | white |
| test | jest, vitest, playwright, mocha | Beaker `F196` | `#8E44AD` | white |
| vim | vim, vi, gvim | MG "Vi" | `#1E8C3F` | white |
| neovim | nvim | MG "Nv" | `#3F9F6F` | white |
| helix | hx, helix | MG "Hx" | `#7A5FD1` | white |
| emacs | emacs, emacsclient | MG "Em" | `#7F5AB6` | white |
| editor | nano, micro, pico, edit | Edit `E70F` | `#6B7A8F` | white |
| vscode | code, code-insiders | Code `E943` | `#2B7BD0` | white |
| git | git, lazygit, gitui, tig, jj | FS Branch Fork | `#F05033` | white |
| github | gh | MG "gh" | `#24292F` | white |
| monitor | htop, btop, top, btm, glances, nvtop | Diagnostic `E9D9` | `#22A06B` | white |
| container | docker, docker-compose, lazydocker, podman | Package `E7B8` | `#1D63ED` | white |
| kube | kubectl, k9s, helm, kubectx | MG "k8" | `#326CE5` | white |
| ssh | ssh, mosh, et, autossh | Remote `E8AF` | `#5B5FC7` | white |
| tailscale | tailscale | Network `E968` | `#4B5563` | white |
| pager | less, more, bat, most | ReadingMode `E736` | `#A0712A` | white |
| manual | man, tldr, info | Dictionary `E82D` | `#A0712A` | white |
| logs | tail, journalctl, lnav, multitail | BulletedList `E8FD` | `#6B7280` | white |
| wait | sleep, watch, timeout | Clock `E917` | `#6B7280` | white |
| database | psql, pgcli, mysql, sqlite3, redis-cli, duckdb, mongosh | FS Database | `#336791` | white |
| http | curl, wget, http, xh | Globe `E774` | `#0E8A8A` | white |
| infra | terraform, tofu, pulumi, ansible, az, aws, gcloud | Cloud `E753` | `#7B42BC` | white |
| claude | claude, claude-code | FS Sparkle | `#D97757` | white |
| codex | codex | FS Bot | `#2B2B2E` | white |
| gemini | gemini | FS Sparkle (gradient `#4796E3` to `#9177C7`) | `#5B7FD8` | white |
| opencode | opencode | MG "oc" | `#3A3F4B` | white |
| copilot | copilot, gh-copilot | Robot `E99A` | `#8250DF` | white |
| cursor | cursor-agent | MG "Cu" | `#111111` | white |
| aider | aider | MG "Ai" | `#14B866` | white |
| amp | amp | MG "Am" | `#F34E3F` | white |
| llm | ollama, llama-server, llama-cli | Robot `E99A` | `#4B5563` | white |
| agent | any other program that reports agent status (OSC 7501) | FS Bot | accent | white |
| fun | sl, cmatrix, cowsay, lolcat, asciiquarium | Game `E7FC` | `#E3008C` | white |

**Glyph-mode tints:**
- Dark theme: use the hue lightened to a relative luminance of 0.60.
- Light theme: use the hue darkened to 0.18.
- Both meet 3:1 against the card. Precompute both per key.

### 8.4 Where icons appear

| Surface | Mode | Size |
|---|---|---|
| Tab header | Tile stack (≤ N) | 16 |
| Pane header | Glyph | 16 |
| Sidebar session row | Tile | 20 |
| Sidebar tab row | Tile | 16 |
| Palette / switcher rows | Tile | 20 |
| Activity list rows | Tile | 20 |
| Toast logo | Tile PNG | 48 px |

**Colourful icons** (Appearance): Always (default) / Active tab only / Never. When off, tiles become monochrome: `ControlFillColorDefaultBrush` with the glyph in Secondary.

### 8.5 Status badges (the "what's going on" icons)

An `InfoBadge` sits on the bottom-right of the first tile: 10 epx at a 16 tile, 12 epx at a 20 tile, offset +3/+3, with a 1.5 epx ring in the surface colour. It uses the built-in WinUI styles:

| State | InfoBadge style | Meaning |
|---|---|---|
| Working (agent busy, no %) | `AttentionIconInfoBadgeStyle`, `IconSource` = `FontIconSource` More `E712` | Accent dot with "…", as on Rex Mac |
| Needs you (input, approval, question, sign-in) | `CautionIconInfoBadgeStyle` | Caution "!" |
| Error | `CriticalIconInfoBadgeStyle` | Critical "×" |
| Done (unseen) | `SuccessIconInfoBadgeStyle` | Green check |
| Idle / seen | none | — |

Badges are static: no pulsing. The tab's words (§3.4) carry the meaning in text, so colour is never the only signal.

### 8.6 User icon packs

`%LOCALAPPDATA%\Packages\<pfn>\LocalState\Icons\<key>.svg` or `.png` replaces a key's tile:
- SVG loads through `SvgImageSource`; PNG through `BitmapImage`.
- Both are drawn in an `ImageIcon`, with no tint.

`icons.json` in the same folder can add aliases (`{"aliases": {"mytool": "build"}}`).

---

## 9. Surfaces and dialogs

All dialogs are `ContentDialog`s:
- `XamlRoot` set, `DefaultContentDialogStyle`, corner radius 8.
- The safe action is focused.
- Esc always means Cancel / Keep running.

Light-dismiss surfaces are `Flyout`s or `Popup`s on `AcrylicInAppFillColorDefaultBrush`. Acrylic is used only on transient surfaces, as Fluent intends.

### 9.1 Session switcher and command palette

This is one control, `RexPalette`, hosted in a `Popup`:
- `IsLightDismissEnabled`, `ShouldConstrainToRootBounds`.
- **Centred variant:** horizontally centred on the window, top edge 56 epx from the window top.
- **Anchored variant** (from the identity chip): under the chip, 360 wide.

**Surface:**
- Width **600**; height up to 520 or 70% of the window.
- `AcrylicInAppFillColorDefaultBrush`, corner radius 8, 1 epx `SurfaceStrokeColorFlyoutBrush`, `ThemeShadow` with `Translation` Z 32.

**Layout:**
1. **Search field:** a 44 tall borderless `TextBox`, 14 epx text, with a leading Search `E721` icon (16 epx, Secondary). A 1 epx `DividerStrokeColorDefaultBrush` line below.
2. **List:** a `ListView` (`SelectionMode="Single"`, `IsSourceGrouped` grouping).
   - Group headers: 28 tall, 12 epx SemiBold, `TextFillColorSecondaryBrush`, with a host-kind glyph for host groups.
   - Rows: 36 tall, with the standard ListViewItem selection (subtle fill and accent pill). Row contents:
     - a tile (20) and a 10 epx gap;
     - the primary label (14);
     - a secondary label (12, Tertiary), e.g. `3 tabs · duck`;
     - the activity word;
     - keycaps, right-aligned;
     - a 14 epx column for ChevronRight `E76C` on rows that open a second step.
3. **Footer:** 32 tall, with a 1 epx divider above. It shows keycap hints in 12 epx Tertiary: `Enter Open · Ctrl+Enter Create · Esc Close`.

**Modes:**
- **Switcher** (Ctrl+Shift+K, or Ctrl+K when opted in):
  - Placeholder: `Switch session, or type a name to create one`.
  - Groups: one per host, with that host's sessions. The current session has CheckMark `E73E`. Rows for offline hosts are disabled and show the word.
  - Typing filters sessions and tabs (a "Tabs" group appears).
  - With no exact match, the first row is `Create session "{text}" on {host}` (Add `E710`).
  - Fixed last rows: New session… (`Ctrl+Shift+N`) and Add host… (Network `E968`).
- **Commands** (Ctrl+Shift+P, or typing `>` in the switcher):
  - Placeholder: `Type a command`.
  - Groups, in order: Session, Tab, Pane, Host, View, App.
  - Each row: a 16 epx glyph in Secondary, the label and its keycaps.
  - Commands that can't run stay in place, disabled.
  - Destructive commands come last in their group.

**Keycaps** use `RexKeycapStyle`:
- Height 20, `MinWidth` 22, padding 6,0, corner radius 4.
- `ControlFillColorDefaultBrush`, 1 epx `ControlStrokeColorDefaultBrush` with a 2 epx bottom border in `ControlStrokeColorSecondaryBrush`.
- 11 epx text in Secondary; 4 epx between caps.

**Focus:** the field is focused on open, and focus returns to the pane on close.

### 9.2 Add host

A `ContentDialog` titled **"Add host"**. Its `ContentDialogMaxWidth` resource is set to **640**, and the content is **600** wide.

**Top:** a `SelectorBar` with two items:
- **On your tailnet** (Network `E968`)
- **Enter address** (Link `E71B`)

**On your tailnet** view:
- **Search:** an `AutoSuggestBox` with placeholder `Search machines`.
- **Machine list:** a grouped `ListView`, about 340 tall. Groups: **Rex servers**, **Reachable over SSH**, **Offline**. Rows are 52 tall:
  - a kind glyph at 20 epx;
  - the machine name (14 SemiBold) over its MagicDNS name and OS (12, Secondary);
  - a trailing check result (12): `Rex 0.1.0 · HTTPS`, `Tailscale SSH`, or `No Rex found` in caution. A `ProgressRing` (16) shows while the check is running;
  - an **"Added"** tag (`Border`, radius 4, `SubtleFillColorSecondaryBrush`, 11 epx) when the machine is already saved, with the saved names as its tooltip.
- **Data sources:** machines come from `tailscale.exe status --json` (`%ProgramFiles%\Tailscale`). Each online machine is probed for a Rex server at `https://<machine>` on 443, then on 7297 over HTTPS and HTTP, reading `server.status` and never attaching.
- **Tailscale absent:** an `InfoBar` (Informational, not closable): title "Tailscale isn't running on this PC", message "Sign in to Tailscale to find your machines, or enter an address.", and an action `HyperlinkButton` "Open Tailscale".

**Selecting a machine** moves to step 2 inside the same dialog. The content slides with an entrance of 167 ms and the dialog title becomes "Connect to duck".
- **Rex server:**
  - the endpoint, read-only;
  - an `Expander` "Server details" with the listeners and admission rules (API 12+);
  - **Name**, as a `TextBox`;
  - **Icon**, as a `ComboBox` of kinds (§6.3).
- **SSH:**
  - **User**, as a `TextBox`, prefilled with the last-used user or `Environment.UserName` in lower case;
  - **Rex socket**, as a `TextBox` with placeholder `Detect automatically`;
  - an `InfoBar` (Informational): "Tailscale SSH is on for duck. No keys needed.", or "Uses your SSH keys and ssh-agent.";
  - **Name** and **Icon**.

**Enter address** view:
- **Address:** a `TextBox` with header "Address" and placeholder `ssh://user@machine  or  https://machine.tailnet.ts.net`. Validation shows in the TextBox `Description` in critical colour: "Use ssh://, https:// or http://".
- **Name** and **Icon** fields.
- A footnote (12, Tertiary): "Rex doesn't authenticate connections. Use a trusted network such as Tailscale, or SSH."

**Buttons:**
- Primary **"Add host"**: `DefaultButton`, enabled only when the form is valid.
- Secondary **"Back"**: step 2 only.
- Close **"Cancel"**.

**When Add is pressed:**
1. The dialog stays open with an indeterminate `ProgressBar` at its top and the text "Connecting…".
2. **Success:** it closes, and the host appears in the sidebar and becomes current.
3. **Failure:** an `InfoBar` (Error) at the top shows the reason verbatim, for example `ssh: Permission denied (publickey)`. Secondary becomes **"Add anyway"**.

**No server on an SSH host:** a separate dialog asks "Start a Rex server on duck?". It names the command it will run once. The default is **Not now**; the primary is "Start server". Nothing is ever started automatically.

### 9.3 Connection and authentication errors

Errors appear in three places:
1. **Workspace InfoBar:** for the host on screen, at the top of the workspace layer, above the panes, with 8 epx side margins.
   - `Severity="Warning"`, title "Reconnecting to duck…", a `ProgressRing` (16) in `Content`, not closable.
   - `Severity="Error"`, title "Can't connect to duck", message `{ssh/HTTP reason}`. `ActionButton` = `Button` "Retry", plus a "Edit host…" `HyperlinkButton` in Content. Closable.
   - There is no success InfoBar. When the host recovers, the bar disappears.
2. **Pane exception strip** (§5.2): input paused in panes whose transport failed. Input is never queued or replayed.
3. **Sidebar host word** (§6.4).

### 9.4 New session

**Fast path:** the switcher's `Create session "…"` row.

**Dialog** (Ctrl+Shift+N, or New session… from menus): a `ContentDialog` titled **"New session on {host}"**; the title updates with the host choice.
- **Host:** a `ComboBox` of connected hosts (kind glyph and name), defaulting to the current host. Offline hosts are disabled.
- **Name:** a `TextBox` with placeholder `Leave empty to let Rex name it`.
- **Start in:** a `TextBox` with placeholder `Home folder` and a `Button` "Browse…". The button opens the remote folder browser: a `Flyout` with a `BreadcrumbBar` for the remote path, a `ListView` of folders (Folder `E8B7`) and a "Use this folder" button.
- Buttons: Primary **"Create"** (`DefaultButton`) and Close "Cancel".

Native local pickers are **not** used here, because the path is on the remote host.

### 9.5 Rename (tab, pane, session)

Renaming is **inline**, Explorer-style:
- A tab label, pane title or sidebar row swaps for a `TextBox` of the same height, font and colour, with all text selected.
- Enter commits, Esc cancels, and losing focus commits.
- **Triggers:**
  - Double-click on the label.
  - F2 while the tab strip or sidebar has keyboard focus. F2 is never taken from a focused terminal.
  - Rename tab… in a menu or the palette.
- **Reset to default title** appears in the tab and pane menus only for renamed items.
- When the sidebar is hidden, renaming a session uses a small `ContentDialog` with one `TextBox`.

### 9.6 Close confirmation

A `ContentDialog`:
- **Title:** `Close tab "build"?` (or pane or session).
- **Content:**
  - "This ends the programs running in it on **duck**:"
  - Up to four rows of tile (16) and command (`claude`, `npm run dev`), then "and 2 more".
  - A `CheckBox`: "Don't ask when only a shell is running".
- **CloseButtonText "Keep running"** with `DefaultButton = ContentDialogButton.Close`. It gets the accent style and initial focus, and Esc chooses it.
- **PrimaryButtonText "Close tab"** with `PrimaryButtonStyle = RexCriticalButtonStyle`: a standard button with `SystemFillColorCriticalBrush` text.

Closing the **window** never asks, because programs keep running on their servers.

### 9.7 Context menus

In the lists below, `|` marks a `MenuFlyoutSeparator`.

**Tab** (`TabViewItem.ContextFlyout`, a `MenuFlyout`): Rename tab `E8AC` (F2) · Mute notifications `E74F` / Unmute `E767` | Move left · Move right · Move to new window `E8A7` | New tab `E710` (Ctrl+Shift+T) | Close other tabs… `E711` · Close tab… `E711` (Ctrl+F4).

**Pane** (right-click in the terminal, or More in the header): a **`CommandBarFlyout`**, the modern Windows text context menu.
- **PrimaryCommands** (icon row): Copy `E8C8` (disabled with no selection) · Paste `E77F` · Select all `E8B3` · Search terminal `E721`.
- **SecondaryCommands** (`AppBarButton`s with `KeyboardAcceleratorTextOverride`): Split right · Split left (`RexSplitRightPath`) · Split down · Split up (`RexSplitDownPath`) · Zoom pane `E740` | Rename pane… `E8AC` · Copy pane ID `E8C8` · Insert path… `E8E5` · Go to directory… `E838` · Download file… `E896` · Upload files… `E898` | Clear terminal `E75C` · Reset terminal `E72C` · Reconnect `E895` | Close pane… `E711`.

The pane a menu acts on shows a 2 epx accent target outline until the menu or its dialog closes.

**Destructive items:** last in their menu after a separator, in normal text colour (Windows convention). They always confirm.

### 9.8 Activity flyout

A `Flyout` from the bell (`Placement="BottomEdgeAlignedRight"`), 400 wide with a max height of 480.

- **Header row:** "Activity" (14 SemiBold) and a `Segmented` control: By priority · By session.
- **Filter:** an `AutoSuggestBox` with placeholder `Filter`.
- **Grouped `ListView`:**
  - **By priority** groups: Needs you, Error, Done, Working.
  - **By session** groups: `{session} · {host}`.
- **Rows:** 52 tall.
  - Left: a tile (20) with its badge, and the tab title (13) over `{session} · {host}` (12 Secondary).
  - Right: the state word (12 SemiBold, coloured) over the elapsed time (`now`, `4m`, `2h`; 11 Tertiary).
- **Empty:** "Nothing needs you." (13 Secondary, centred).
- **Click:** switches to the tab, focuses the pane and closes the flyout. Seeing a row does not mark it seen.

### 9.9 Files: native pickers, downloads and uploads

Every local file and folder choice uses the **native Windows pickers** from `Microsoft.Windows.Storage.Pickers` (WinAppSDK 1.8+). They are constructed with `new FileSavePicker(window.AppWindow.Id)` and so on, which needs no `InitializeWithWindow` and works when elevated.

If the older `Windows.Storage.Pickers` must be used, initialise each picker with `WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window))`.

| Action | Picker | Settings |
|---|---|---|
| Download file… → Save as… | `FileSavePicker` | `SuggestedFileName` = remote name; `FileTypeChoices` from its extension; `SuggestedStartLocation = PickerLocationId.Downloads` |
| Download folder (Settings → General) | `FolderPicker` | `SuggestedStartLocation = Downloads` |
| Import terminal theme | `FileOpenPicker` | `FileTypeFilter`: `.json`, `.itermcolors`, `.conf`, `*` |
| Export settings / shortcuts | `FileSavePicker` | `.json` |
| Upload files… (pane menu) | `FileOpenPicker.PickMultipleFilesAsync` | — |

**Download file…** is a `ContentDialog` titled "Download from duck" with:
- **File on duck:** a `TextBox`, prefilled from a path-like selection.
- **Save to:** the folder path (middle-trimmed, full path in its tooltip) and a "Change…" button.
- Buttons: Primary "Download", Secondary "Save as…", Close "Cancel".
- A file ≥ 1 GiB, or larger than the free space, asks inside the dialog with **Cancel** focused.

**Downloads button** (§3.5): a `Flyout`, 400 wide, with one row per transfer: tile and name, a `ProgressBar`, Cancel while running, and **Show in folder** (`Launcher.LaunchFolderPathAsync(folder, new FolderLauncherOptions { ItemsToSelect = { file } })`) once finished. A file is **never opened or run**.

**Uploads:**
- Dragging files from Explorer onto a card shows a drop overlay: 2 epx `AccentFillColorDefaultBrush` stroke, corner radius 8, `AcrylicInAppFillColorDefaultBrush` at 80%, centred Upload `E898` (32) and "Drop to upload to duck · ~/Sites/rex-snake".
- Dropped files (`DataPackageView.GetStorageItemsAsync`) and pasted files are uploaded, and their quoted remote paths are typed at the prompt. Enter is never sent.

### 9.10 Notifications

**Toasts** use `Microsoft.Windows.AppNotifications`:
- Subscribe `AppNotificationManager.Default.NotificationInvoked`, **then** call `Register()`.
- Each toast is built with `AppNotificationBuilder`. Its attribution is `SetAttributionText("{session} · {host}")` and its logo is `SetAppLogoOverride(tilePng, AppNotificationImageCrop.Default)`.
- After `BuildNotification()`, set `Tag = tabId` and `Group = sessionId`, so a newer toast replaces the older one for the same tab.
- When a tab is seen, call `RemoveByTagAndGroupAsync(tabId, sessionId)`.
- No toast is sent for a tab that is visible in the focused window.

| Kind | Title | Body | Buttons (arguments) | Priority / sound | Default |
|---|---|---|---|---|---|
| Needs input / approval / question / sign-in | `{tab} needs input` (or approval, etc.) | Program message | **Open** (`action=open;host;session;tab`), **Mute tab** | `Priority = High` · Default sound | On |
| Error | `{tab} hit an error` | Program message | Open | High · Default | On |
| Done | `{tab} is done` | Program message or "Finished" | Open | Default · silent | On |
| Bell | `Bell in {tab}` | — | Open | Default · silent | Off |
| Program notification (OSC 9 / 777) | Program's title | Program's body | Open | Default · silent | On |

Clicking **Open** brings the window forward (`AppWindow.Show()`, `SetForegroundWindow`), switches host, session and tab, and focuses the pane.

**In-app notices** (font-size changes, finished downloads, remote-control actions):
- `InfoBar`s stacked at the workspace's **bottom-right**, 12 epx from the edges, 360 wide, at most 3.
- `ThemeShadow` at Z 32.
- Informational notices dismiss after 6 s; Error notices stay until closed.

### 9.11 Settings window

A separate `Window` (`SettingsWindow`), one instance only:
- 1040 × 720, minimum 720 × 520, backdrop `MicaBackdrop { Kind = MicaKind.Base }`.
- The **WinUI `TitleBar` control** with `IconSource` set to the app icon, `Title="Settings"` and `IsBackButtonVisible="{x:Bind ContentFrame.CanGoBack}"`. Set `PreferredHeightOption = Standard` (32).

**Navigation:** a `NavigationView` with:
- `PaneDisplayMode="Left"`, `IsBackButtonVisible="Collapsed"`, `IsSettingsVisible="False"`, `OpenPaneLength="260"`.
- `AutoSuggestBox` placeholder `Search settings`. It searches a static index of every card's Header and Description, and opening a result scrolls to the card and highlights it with `SubtleFillColorSecondary` for 2.4 s.
- `ContentFrame` with `EntranceNavigationTransitionInfo`; search jumps use `SuppressNavigationTransitionInfo`.
- Nav items:

| Item | Glyph |
|---|---|
| General | Settings `E713` |
| Appearance | Personalize `E771` |
| Terminal | CommandPrompt `E756` |
| Shortcuts | KeyboardShortcut `EDA7` |
| Remote hosts | Network `E968` |
| Notifications | Ringer `EA8F` |
| About | Info `E946` |

**Page layout:**
- Page `Padding` 36,24,36,36 and `MaxWidth` 1000.
- Section headers use `BodyStrongTextBlockStyle` with a 28 epx top margin.
- Cards are `SettingsCard` / `SettingsExpander` (CommunityToolkit) with `HeaderIcon` as a `FontIcon`, 4 epx apart (`SettingsCardSpacing`).
- Unavailable settings are disabled, never hidden, and their Description says why.

**Pages** (card: control, default):

| Page | Cards |
|---|---|
| General | Restore last session on launch: `ToggleSwitch`, on (never creates sessions) · Default host for new sessions: `ComboBox` · Launch at sign-in: `ToggleSwitch`, off (`StartupTask`) · Ask before closing tabs and panes: `SettingsExpander` containing "Don't ask when only a shell is running" · Links that open other apps: `ComboBox` Ask first / Always / Never · Download folder: `Button` "Change…" → `FolderPicker` · Updates: read-only version and a "Check for updates" `HyperlinkButton` to the Store page |
| Appearance | App theme: `ComboBox` · Window material: `SettingsExpander` with `RadioButtons` (Mica Alt (recommended), Mica, Acrylic, Solid) containing Glass opacity (`Slider` 40–100) and Keep glass when inactive (`ToggleSwitch`) · Workspace tint: `ComboBox` · Accent colour: `ToggleSwitch` "Use Windows accent" plus a `GridView` of 8 swatches · Pane style: `Segmented` Comfortable · Compact · Pane headers: `ComboBox` · Dim inactive panes: `ComboBox` · Highlight focused pane: `ToggleSwitch` · Tab icons: `Slider` 0–5 with a live sample tab drawn by the real tab template · Colourful icons: `ComboBox` · Sidebar position: `Segmented` Left · Right · Show tabs in: `Segmented` Title bar · Sidebar · Animations: `ComboBox` Snappy (default) / Natural / Off, forced Off when `UISettings.AnimationsEnabled` is false |
| Terminal | Font (Choose… flyout), Size (`NumberBox`, inline spin buttons), Line height (`NumberBox`, 0.05 steps) · Preview well: 8 epx-radius `Border` on `CardBackgroundFillColorSecondaryBrush` holding a `CanvasControl` with a 3-line sample from the real renderer · Fallback fonts: `SettingsExpander` with a reorderable `ListView` · Text antialiasing · Cursor shape, blink, force shape, cursor uses accent colour · Dark theme, light theme, Import theme… · Selection: copy on select, rounded corners · Padding horizontal and vertical: `NumberBox` 0–32 · Smooth scrolling: off · Warn before pasting multiple lines: on · Bell: `ComboBox` Off / Windows sound / Flash taskbar (default Flash taskbar) · Prefer generated titles |
| Shortcuts | Pinned `AutoSuggestBox` "Filter shortcuts", matching names, groups and keys (`ctrl+shift+k`) · `SettingsCard`s grouped as Window, Tabs, Panes, Sessions, Navigation, Terminal, App; each card's Content is a keycap `Button`, and an unbound action shows "Record shortcut" in Tertiary · **Recording:** clicking a binding shows `Press keys…` with a 2 epx accent border; Esc cancels, Backspace removes, and a conflict shows a warning `InfoBar` in the card before saving · **Use Ctrl+K for the session switcher**: `ToggleSwitch`, off, description "Programs in the terminal will no longer receive Ctrl+K." · **Reset all…** asks first, defaulting to Keep shortcuts |
| Remote hosts | Accent `Button` "Add host" at the top · Tailscale status card, read-only ("Signed in as … · tailnet …", with Copy) · One `SettingsExpander` per host: Header the name, `HeaderIcon` the kind glyph, Description the endpoint plus any trouble word; items Name, Address, Icon, Connect at launch (`ToggleSwitch`), **Remote control** (`ToggleSwitch`, off: "Let other Rex clients run view actions in this app"), Remove host… (`RexCriticalButtonStyle`) |
| Notifications | "Windows notifications for Windex" with a `HyperlinkButton` "Open Windows notification settings" (`ms-settings:notifications`) · One `SettingsExpander` per kind (Needs input, Error, Done, Bell, Working) with its tile and badge, containing Show in tabs, Windows notification, Sound (`ComboBox` None / Default / Reminder / IM / Mail) and Flash taskbar button · Taskbar progress: on · Badge count on taskbar icon: on · Send test notification: `Button` |
| About | Centred block: app icon at 64, "Windex" (`SubtitleTextBlockStyle`), version (Body), `Rex Server 0.1.0-preview.15 on duck` (Caption, Secondary; "not reported" when the server hasn't answered), buttons Copy version info and Open logs folder (`Launcher.LaunchFolderPathAsync`) · Cards: Documentation, Report an issue, Third-party notices (a `ContentDialog` listing Win2D, Windows Community Toolkit, Fluent UI System Icons (MIT), Symbols Nerd Font (MIT plus the bundled sets' licences), Cascadia (OFL, system) and the Rex Rune mapping logic (MIT)) |

### 9.12 First run and empty workspace

- **First launch:** a centred card on the workspace layer (480 wide):
  - Network `E968` at 40;
  - "Connect to a Rex server" (`SubtitleTextBlockStyle`);
  - "Windex shows sessions running on your machines. Pick one on your tailnet or add an address." (Body, Secondary);
  - an accent "Add host" button and a `HyperlinkButton` "Learn about Rex".
- **Once the first host is added:** a `TeachingTip` points at the identity chip: "Switch sessions here, or press Ctrl+Shift+K." Its action button is "Use Ctrl+K instead" (the opt-in).
- **A session with no tabs:** "No tabs in rex-snake" and a "New tab" button.

---

## 10. Motion

Durations and curves come from the WinUI resources:
- `ControlFasterAnimationDuration` 83 ms, `ControlFastAnimationDuration` 167 ms, `ControlNormalAnimationDuration` 250 ms.
- **Decelerate** (entrance): `ControlFastOutSlowInKeySpline` 0,0,0,1.
- **Point to point**: 0.55,0.55,0,1.
- **Gentle exit**: 1,0,1,1.

| Element | Transition | Duration / curve |
|---|---|---|
| Tab add / remove / reorder | TabView built-in item transitions (`AddDeleteThemeTransition`, `RepositionThemeTransition`) | System |
| Selected tab change | Instant fill swap; header text colour instant | — |
| Sidebar open (docked) | `Translation.X` −16 → 0 and Opacity 0 → 1; panes resize once, before the slide | 250 ms, decelerate |
| Sidebar close | Opacity → 0, X → −16; panes resize once, after | 167 ms, gentle exit |
| Sidebar overlay | Same as open/close, plus `ThemeShadow` | same |
| Flyouts, MenuFlyout, CommandBarFlyout, ContentDialog, TeachingTip | Built-in | System |
| Palette open | Opacity 0 → 1, `Translation.Y` −8 → 0 | 167 ms, decelerate |
| Palette close | Opacity → 0 | 83 ms, linear |
| Pane focus change | Card `BackgroundTransition` (`BrushTransition`); dim `OpacityTransition`; lift `TranslationTransition` on Z only | 167 ms |
| Header buttons reveal / hide | `OpacityTransition` | 83 ms |
| Splitter grip | `OpacityTransition` | 83 ms |
| InfoBar / in-app notice enter | Opacity 0 → 1, Y +8 → 0 | 167 ms, decelerate |
| InfoBar / notice exit | Opacity → 0 | 83 ms |
| Add host step change | Content X +24 → 0 with fade | 167 ms, decelerate |
| Find bar corner swap | `Translation` point-to-point | 167 ms |
| Settings page navigation | `EntranceNavigationTransitionInfo` | System |
| Zoom / restore pane, split, close pane | **Instant** | — |
| ProgressBar value | Built-in | System |

**Animation settings:**
- **Natural** multiplies chrome durations by 1.5.
- **Off** or `UISettings.AnimationsEnabled == false` sets every custom transition to `null` and duration 0. The built-in theme transitions already respect the system setting.

**Rule: terminal content never animates.**
- No transition, implicit animation or composition animation targets the swap-chain panel, its contents or its X/Y position.
- Resizes re-render at the new size.
- Cursor blink is binary.
- Smooth scrolling is off by default and, when on, moves only the scroll offset.
- The dim overlay and card fill may fade, because they are chrome above or below the content.

---

## 11. Keyboard shortcuts

These defaults follow Windows Terminal conventions: Ctrl+Shift+… for app actions, so plain Ctrl+letter keeps reaching programs.

- Every binding can be rebound or removed in Settings → Shortcuts.
- Keys the app doesn't consume go to the focused pane.
- AltGr (Right Alt) is never treated as Ctrl+Alt.

| Action | Default | Notes |
|---|---|---|
| New tab | Ctrl+Shift+T | In the current session, starting in the focused pane's directory |
| New session… | Ctrl+Shift+N | Rex Mac parity (⇧⌘N) |
| New window | — (palette, Shift+click on the taskbar, Jump List) | |
| Close pane | Ctrl+Shift+W | Confirms; closes the tab if it was the last pane |
| Close tab | Ctrl+F4 | TabView convention; confirms |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab | TabView built-in, within the session |
| Go to tab 1–9 | Ctrl+Alt+1 … 9 | 9 = last (Windows Terminal) |
| Next / previous session | Ctrl+Shift+] / Ctrl+Shift+[ | In sidebar order across hosts |
| Session switcher | **Ctrl+Shift+K** | Ctrl+K opt-in (§9.11) |
| Command palette | Ctrl+Shift+P | |
| Split right | Ctrl+Shift+D | Also Alt+Shift+Plus |
| Split down | Ctrl+Shift+E | Also Alt+Shift+Minus |
| Focus pane left / right / up / down | Alt+Arrow | |
| Resize pane | Alt+Shift+Arrow | 5% steps |
| Zoom / restore pane | Ctrl+Shift+Enter | |
| Find | Ctrl+Shift+F | F3 / Shift+F3 step while open |
| Copy | Ctrl+C (with a selection only) · Ctrl+Shift+C · Ctrl+Insert | |
| Paste | Ctrl+V · Ctrl+Shift+V · Shift+Insert | Multi-line warning optional |
| Scroll line / page / top / bottom | Ctrl+Shift+Up/Down · Ctrl+Shift+PgUp/PgDn · Ctrl+Shift+Home/End | |
| Font size + / − / reset | Ctrl+= · Ctrl+− · Ctrl+0; also Ctrl+wheel | One notice: "Font 12 pt" |
| Toggle sidebar | Ctrl+Shift+B | |
| Activity | Ctrl+Shift+A | |
| Go to directory… | Ctrl+Shift+G | Types `cd <path>`; never Enter |
| Insert path… | — | Palette |
| Rename tab | F2 (tab strip focused) · palette | |
| Cycle focus: tab strip → sidebar → panes | Ctrl+F6 / Ctrl+Shift+F6 | Accessibility path into the chrome |
| Settings | Ctrl+, | |
| Full screen | F11 · Alt+Enter | |
| Escape a panel | Esc | Returns focus to the pane |

Tooltips and menus show bindings in Windows spelling: `Ctrl+Shift+D`. Lists draw them as keycaps (§9.1).

---

## 12. Typography

| Token | Font | Size / weight | Use |
|---|---|---|---|
| RexSessionText | Segoe UI Variable Text | 13 SemiBold | Identity chip, line 1 |
| RexHostText | Segoe UI Variable Text | 11 Regular, Secondary | Identity chip, line 2 |
| RexTabLabel | Segoe UI Variable Text | 12 Regular | Tabs |
| RexActivityWord | Segoe UI Variable Text | 12 SemiBold (11 in the sidebar) | Activity words |
| RexPaneTitle | Segoe UI Variable Text | 13 SemiBold | Pane header |
| RexSidebarHost | Segoe UI Variable Text | 13 SemiBold | Host rows |
| RexSidebarSession | Segoe UI Variable Text | 13 Regular | Session rows |
| Dialog body | `BodyTextBlockStyle` | 14 | Dialogs, Settings |
| Captions | `CaptionTextBlockStyle` | 12 | Helper text, footnotes |
| Monogram | Segoe UI Variable Display | 8.5 / 11 SemiBold | Tiles |
| Terminal | Cascadia Mono | 11 pt | Terminal |

---

## 13. Accessibility and high contrast

- **Automation names:** every icon-only control has `AutomationProperties.Name`, and `AutomationProperties.AcceleratorKey` where it has a shortcut.
  - Tabs announce `"{label}, {activity word}, {progress}%"`.
  - Pane cards are `AutomationControlType.Pane` with the pane title as their name.
- **Live regions:** activity words and InfoBars use `AutomationProperties.LiveSetting="Polite"`; errors use Assertive.
- **Colour is never the only signal:** words, badges and glyph shapes carry the meaning too.
- **Contrast:** text meets 4.5:1 on its surface, and glyphs and strokes 3:1. Check with the Solid column as the worst case for translucency.
- **High contrast** (`AccessibilitySettings.HighContrast`, ThemeDictionary `HighContrast`):
  - The aurora, materials, shadows and dim are off.
  - Cards are `SystemColorWindowColor` with a 1 epx `SystemColorWindowTextColor` stroke; the focused card gets a **2 epx `SystemColorHighlightColor`** stroke.
  - Unfocused headers use `SystemColorGrayTextColor`.
  - The terminal declares a "High contrast" theme built from the system colours.
- **Text scaling** (`UISettings.TextScaleFactor`) scales chrome text; fixed heights grow with it via `MinHeight`. The terminal font is independent.
- **Focus visuals:** system reveal focus visuals stay on everywhere except the terminal, which shows focus with its cursor and the card stroke.

---

## 14. Tokens

L = light, D = dark. "Maps to" names the WinUI resource used directly, or the one the custom value derives from.

| Token | Light | Dark | Maps to / derived from |
|---|---|---|---|
| RexTitleBarHeight | 48 | 48 | `TitleBarHeightOption.Tall` |
| RexPaneCornerRadius | 8 | 8 | `OverlayCornerRadius` |
| RexControlCornerRadius | 4 | 4 | `ControlCornerRadius` |
| RexTabCornerRadius | 8,8,0,0 | 8,8,0,0 | `OverlayCornerRadius` (top only) |
| RexPaneGap | 6 | 6 | — |
| RexPaneAreaPadding | 8 | 8 | — |
| RexPaneHeaderHeight | 36 | 36 | — |
| RexSidebarWidth | 260 (200–360) | same | — |
| RexWorkspaceLayerBrush | `#73FFFFFF` | `#4D2C2C2C` | Derived from `LayerOnMicaBaseAltFillColorDefaultBrush` (`#B3FFFFFF` / `#733A3A3A`), lightened so the aurora reads |
| RexWorkspaceStrokeBrush | `#0F000000` | `#19000000` | `CardStrokeColorDefaultBrush` |
| RexAuroraBrush | stops `#8CF5C6EA` → `#40D8DBF8` → `#8CFFE4A0` | `#4D7B4F8F` → `#1F404A7A` → `#40A06A3A` | Custom `LinearGradientBrush` |
| RexPaneCardBrush | `#F2FFFFFF` | `#E61F1E24` | Derived from `CardBackgroundFillColorDefaultBrush` (`#B3FFFFFF` / `#0DFFFFFF`), more opaque for text |
| RexPaneCardInactiveBrush | `#A6FFFFFF` | `#A61F1E24` | as above |
| RexPaneStrokeBrush | `#0F000000` | `#1AFFFFFF` | L: `CardStrokeColorDefaultBrush`; D: custom white rim |
| RexPaneFocusStrokeBrush | `SystemAccentColorDark1` at 0.8 | `SystemAccentColorLight2` at 0.7 | `AccentFillColorDefaultBrush` |
| RexPaneDimBrush | `#4DFAF9FC` | `#47000000` | Custom (`SmokeFillColorDefaultBrush` is `#4D000000`) |
| RexPaneFocusedDepth | Z 8 | Z 8 | `ThemeShadow` |
| RexOverlayDepth | Z 32 | Z 32 | `ThemeShadow` (flyout depth) |
| RexOverlayBrush | Tint `#FCFCFC`, lum 0.85, fallback `#F9F9F9` | Tint `#2C2C2C` at 0.15, lum 0.96, fallback `#2C2C2C` | `AcrylicInAppFillColorDefaultBrush` |
| RexOverlayStrokeBrush | `#0F000000` | `#33000000` | `SurfaceStrokeColorFlyoutBrush` |
| RexDividerBrush | `#0F000000` | `#15FFFFFF` | `DividerStrokeColorDefaultBrush` |
| RexTextPrimary | `#E4000000` | `#FFFFFF` | `TextFillColorPrimaryBrush` |
| RexTextSecondary | `#9E000000` | `#C5FFFFFF` | `TextFillColorSecondaryBrush` |
| RexTextTertiary | `#72000000` | `#87FFFFFF` | `TextFillColorTertiaryBrush` |
| RexSubtleHover | `#09000000` | `#0FFFFFFF` | `SubtleFillColorSecondaryBrush` |
| RexSubtlePressed | `#06000000` | `#0AFFFFFF` | `SubtleFillColorTertiaryBrush` |
| RexSelectionIndicator | `SystemAccentColorDark1` | `SystemAccentColorLight2` | `AccentFillColorDefaultBrush` |
| RexAttentionWord | `#9D5D00` | `#FCE100` | `SystemFillColorCautionBrush` |
| RexErrorWord | `#C42B1C` | `#FF99A4` | `SystemFillColorCriticalBrush` |
| RexDoneWord | `#0F7B0F` | `#6CCB5F` | `SystemFillColorSuccessBrush` |
| RexWorkingWord | `#72000000` | `#87FFFFFF` | `TextFillColorTertiaryBrush` (Activity list only) |
| RexProgressBrush | accent | accent | `AccentFillColorDefaultBrush` (ProgressBar default) |
| RexKeycapFill | `#B3FFFFFF` | `#0FFFFFFF` | `ControlFillColorDefaultBrush` |
| RexKeycapStroke | `#0F000000` (bottom `#29000000`) | `#12FFFFFF` (bottom `#18FFFFFF`) | `ControlStrokeColorDefaultBrush` / `ControlStrokeColorSecondaryBrush` |
| RexTileSheen | `#38FFFFFF` → `#00FFFFFF` | same | Custom |
| RexTerminalBackground | `#FBFAFC` (not painted) | `#1B1A1F` (not painted) | Rex Dawn / Dusk |
| RexTerminalForeground | `#24212B` | `#E7E4EC` | Rex Dawn / Dusk |
| RexTerminalSelection | `#C9DDFB` | `#3A4B70` | Rex Dawn / Dusk |
| RexTerminalCursor | `SystemAccentColorDark1` (`#0067C0` fallback) | `SystemAccentColorLight2` (`#8AB4FF` fallback) | Accent |

The Acrylic and Solid values for the workspace layer and cards are in §4.2. The app swaps them in code by replacing the brushes' `Color` when the material changes.

---

## 15. `Themes/Rex.xaml` (sketch)

Merge this after `XamlControlsResources` in `App.xaml`. Values the material switch replaces at runtime are marked.

```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
      <SolidColorBrush x:Key="RexWorkspaceLayerBrush"   Color="#73FFFFFF"/>   <!-- material-swapped -->
      <SolidColorBrush x:Key="RexPaneCardBrush"         Color="#F2FFFFFF"/>   <!-- material-swapped -->
      <SolidColorBrush x:Key="RexPaneCardInactiveBrush" Color="#A6FFFFFF"/>   <!-- material-swapped -->
      <SolidColorBrush x:Key="RexPaneStrokeBrush"       Color="#0F000000"/>
      <SolidColorBrush x:Key="RexPaneFocusStrokeBrush"  Color="{ThemeResource SystemAccentColorDark1}" Opacity="0.8"/>
      <SolidColorBrush x:Key="RexPaneDimBrush"          Color="#4DFAF9FC"/>
      <LinearGradientBrush x:Key="RexAuroraBrush" StartPoint="0,0" EndPoint="1,1">
        <GradientStop Offset="0"   Color="#8CF5C6EA"/>
        <GradientStop Offset="0.5" Color="#40D8DBF8"/>
        <GradientStop Offset="1"   Color="#8CFFE4A0"/>
      </LinearGradientBrush>
      <!-- Tab lightweight styling (keys from TabView_themeresources.xaml) -->
      <StaticResource x:Key="TabViewBackground"                         ResourceKey="ControlFillColorTransparentBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackground"               ResourceKey="ControlFillColorTransparentBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundSelected"       ResourceKey="RexWorkspaceLayerBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundPointerOver"    ResourceKey="SubtleFillColorSecondaryBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundPressed"        ResourceKey="SubtleFillColorTertiaryBrush"/>
      <StaticResource x:Key="TabViewItemHeaderForeground"               ResourceKey="TextFillColorSecondaryBrush"/>
      <StaticResource x:Key="TabViewItemHeaderForegroundSelected"       ResourceKey="TextFillColorPrimaryBrush"/>
    </ResourceDictionary>

    <ResourceDictionary x:Key="Dark">
      <SolidColorBrush x:Key="RexWorkspaceLayerBrush"   Color="#4D2C2C2C"/>
      <SolidColorBrush x:Key="RexPaneCardBrush"         Color="#E61F1E24"/>
      <SolidColorBrush x:Key="RexPaneCardInactiveBrush" Color="#A61F1E24"/>
      <SolidColorBrush x:Key="RexPaneStrokeBrush"       Color="#1AFFFFFF"/>
      <SolidColorBrush x:Key="RexPaneFocusStrokeBrush"  Color="{ThemeResource SystemAccentColorLight2}" Opacity="0.7"/>
      <SolidColorBrush x:Key="RexPaneDimBrush"          Color="#47000000"/>
      <LinearGradientBrush x:Key="RexAuroraBrush" StartPoint="0,0" EndPoint="1,1">
        <GradientStop Offset="0"   Color="#4D7B4F8F"/>
        <GradientStop Offset="0.5" Color="#1F404A7A"/>
        <GradientStop Offset="1"   Color="#40A06A3A"/>
      </LinearGradientBrush>
      <StaticResource x:Key="TabViewBackground"                         ResourceKey="ControlFillColorTransparentBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackground"               ResourceKey="ControlFillColorTransparentBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundSelected"       ResourceKey="RexWorkspaceLayerBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundPointerOver"    ResourceKey="SubtleFillColorSecondaryBrush"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundPressed"        ResourceKey="SubtleFillColorTertiaryBrush"/>
      <StaticResource x:Key="TabViewItemHeaderForeground"               ResourceKey="TextFillColorSecondaryBrush"/>
      <StaticResource x:Key="TabViewItemHeaderForegroundSelected"       ResourceKey="TextFillColorPrimaryBrush"/>
    </ResourceDictionary>

    <ResourceDictionary x:Key="HighContrast">
      <SolidColorBrush x:Key="RexWorkspaceLayerBrush"   Color="{ThemeResource SystemColorWindowColor}"/>
      <SolidColorBrush x:Key="RexPaneCardBrush"         Color="{ThemeResource SystemColorWindowColor}"/>
      <SolidColorBrush x:Key="RexPaneCardInactiveBrush" Color="{ThemeResource SystemColorWindowColor}"/>
      <SolidColorBrush x:Key="RexPaneStrokeBrush"       Color="{ThemeResource SystemColorWindowTextColor}"/>
      <SolidColorBrush x:Key="RexPaneFocusStrokeBrush"  Color="{ThemeResource SystemColorHighlightColor}"/>
      <SolidColorBrush x:Key="RexPaneDimBrush"          Color="Transparent"/>
      <SolidColorBrush x:Key="RexAuroraBrush"           Color="Transparent"/>
      <StaticResource x:Key="TabViewItemHeaderBackgroundSelected"       ResourceKey="SystemColorHighlightColorBrush"/>
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>

  <!-- Geometry and spacing -->
  <x:Double x:Key="RexTitleBarHeight">48</x:Double>
  <x:Double x:Key="RexPaneHeaderHeight">36</x:Double>
  <x:Double x:Key="RexPaneGap">6</x:Double>
  <Thickness x:Key="RexPaneAreaPadding">8</Thickness>
  <x:Double x:Key="RexSidebarWidth">260</x:Double>
  <x:Double x:Key="TabViewItemMinWidth">120</x:Double>
  <CornerRadius x:Key="RexTabCornerRadius">8,8,0,0</CornerRadius>
  <!-- ContentDialogMaxWidth = 640 goes in AddHostDialog.Resources only, never here (it would widen every dialog) -->

  <!-- Authored icons (16x16, filled; EvenOdd gives 1 epx outlines) -->
  <x:String x:Key="RexSplitRightPath">F0 M3,2 H13 A2,2 0 0 1 15,4 V12 A2,2 0 0 1 13,14 H3 A2,2 0 0 1 1,12 V4 A2,2 0 0 1 3,2 Z M3,3 A1,1 0 0 0 2,4 V12 A1,1 0 0 0 3,13 H7.5 V3 Z M8.5,3 H13 A1,1 0 0 1 14,4 V12 A1,1 0 0 1 13,13 H8.5 Z</x:String>
  <x:String x:Key="RexSplitDownPath">F0 M3,2 H13 A2,2 0 0 1 15,4 V12 A2,2 0 0 1 13,14 H3 A2,2 0 0 1 1,12 V4 A2,2 0 0 1 3,2 Z M3,3 A1,1 0 0 0 2,4 V7.5 H14 V4 A1,1 0 0 0 13,3 Z M2,8.5 V12 A1,1 0 0 0 3,13 H13 A1,1 0 0 0 14,12 V8.5 Z</x:String>
  <x:String x:Key="RexPenguinPath">F0 M8,1.5 C5.6,1.5 4.5,3.6 4.5,6 C4.5,7.4 3.5,8.6 3,10.2 C2.5,12 3.4,14.5 8,14.5 C12.6,14.5 13.5,12 13,10.2 C12.5,8.6 11.5,7.4 11.5,6 C11.5,3.6 10.4,1.5 8,1.5 Z M8,7 C6.6,7 5.8,8.6 5.8,10.4 C5.8,12.2 6.7,13.2 8,13.2 C9.3,13.2 10.2,12.2 10.2,10.4 C10.2,8.6 9.4,7 8,7 Z M6.6,4.2 A0.7,0.7 0 1 0 6.6,5.6 A0.7,0.7 0 1 0 6.6,4.2 Z M9.4,4.2 A0.7,0.7 0 1 0 9.4,5.6 A0.7,0.7 0 1 0 9.4,4.2 Z</x:String>
  <x:String x:Key="RexMiniBoxPath">F0 M3,4 H13 A2,2 0 0 1 15,6 V10 A2,2 0 0 1 13,12 H3 A2,2 0 0 1 1,10 V6 A2,2 0 0 1 3,4 Z M3,5 A1,1 0 0 0 2,6 V10 A1,1 0 0 0 3,11 H13 A1,1 0 0 0 14,10 V6 A1,1 0 0 0 13,5 Z M11.5,8.75 H13 V9.25 H11.5 Z</x:String>
  <!-- PathIcon.Data is a Geometry: create with XamlBindingHelper.ConvertValue(typeof(Geometry), path) or inline <PathIcon Data="..."/> -->

  <!-- Title-bar and pane-header buttons -->
  <Style x:Key="RexTitleBarButtonStyle" TargetType="Button" BasedOn="{StaticResource DefaultButtonStyle}">
    <Setter Property="Width" Value="40"/>
    <Setter Property="Height" Value="36"/>
    <Setter Property="Padding" Value="0"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="CornerRadius" Value="{StaticResource ControlCornerRadius}"/>
    <Setter Property="FontFamily" Value="{ThemeResource SymbolThemeFontFamily}"/>
    <Setter Property="FontSize" Value="16"/>
    <!-- Template VisualStates: PointerOver -> SubtleFillColorSecondaryBrush, Pressed -> SubtleFillColorTertiaryBrush,
         Disabled/WindowInactive foreground -> TextFillColorDisabledBrush -->
  </Style>

  <Style x:Key="RexPaneHeaderButtonStyle" TargetType="Button" BasedOn="{StaticResource RexTitleBarButtonStyle}">
    <Setter Property="Width" Value="28"/>
    <Setter Property="Height" Value="28"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
  </Style>

  <Style x:Key="RexIdentityChipStyle" TargetType="Button" BasedOn="{StaticResource RexTitleBarButtonStyle}">
    <Setter Property="Width" Value="NaN"/>
    <Setter Property="Height" Value="40"/>
    <Setter Property="MinWidth" Value="140"/>
    <Setter Property="MaxWidth" Value="240"/>
    <Setter Property="Padding" Value="8,0,8,0"/>
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="FontFamily" Value="{ThemeResource ContentControlThemeFontFamily}"/>
  </Style>

  <Style x:Key="RexCriticalButtonStyle" TargetType="Button" BasedOn="{StaticResource DefaultButtonStyle}">
    <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}"/>
  </Style>

  <!-- Pane card -->
  <Style x:Key="RexPaneCardStyle" TargetType="Border">
    <Setter Property="CornerRadius" Value="{StaticResource OverlayCornerRadius}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="BorderBrush" Value="{ThemeResource RexPaneStrokeBrush}"/>
    <Setter Property="Background" Value="{ThemeResource RexPaneCardInactiveBrush}"/>
    <Setter Property="BackgroundSizing" Value="InnerBorderEdge"/>
    <!-- In code: BackgroundTransition = new BrushTransition { Duration = 167ms };
         Shadow = sharedThemeShadow; Translation = focused ? (0,0,8) : (0,0,0); TranslationTransition = 167ms (Z only) -->
  </Style>

  <!-- Keycap -->
  <Style x:Key="RexKeycapStyle" TargetType="Border">
    <Setter Property="Height" Value="20"/>
    <Setter Property="MinWidth" Value="22"/>
    <Setter Property="Padding" Value="6,0"/>
    <Setter Property="CornerRadius" Value="{StaticResource ControlCornerRadius}"/>
    <Setter Property="Background" Value="{ThemeResource ControlFillColorDefaultBrush}"/>
    <Setter Property="BorderBrush" Value="{ThemeResource ControlStrokeColorDefaultBrush}"/>
    <Setter Property="BorderThickness" Value="1,1,1,2"/>
  </Style>

  <!-- Text -->
  <Style x:Key="RexTabLabelTextStyle" TargetType="TextBlock">
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
    <Setter Property="MaxLines" Value="1"/>
    <Setter Property="TextWrapping" Value="NoWrap"/>
  </Style>
  <Style x:Key="RexActivityWordTextStyle" TargetType="TextBlock">
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="TextWrapping" Value="NoWrap"/>
  </Style>
  <Style x:Key="RexPaneTitleTextStyle" TargetType="TextBlock" BasedOn="{StaticResource RexTabLabelTextStyle}">
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
  </Style>
</ResourceDictionary>
```

**Notes for developers:**
- The `StaticResource` aliases in ThemeDictionaries are WinUI's lightweight-styling pattern. Check each `TabViewItemHeader*` key against `TabView_themeresources.xaml` for the pinned WinUI version before relying on it.
- The selected-tab top and side stroke, and the 1 epx mask over the layer's top edge, need a copied `TabViewItem` template (`RexTabViewItemStyle`). Keep the rest of the default template unchanged.
- The program tiles' hues and Fg values live in `Themes/ProgramIcons.xaml` as `Color` resources keyed `RexIcon.{key}.Hue`, `.HueLight`, `.Fg`, `.GlyphOnDark` and `.GlyphOnLight`, generated from the §8.3 table.

---

## 16. Acceptance checklist

Each item can be checked from a screenshot of the running app at 100% scale and 1280 × 800, in light and dark mode, unless it says otherwise.

1. The title row is 48 epx tall and shows, left to right: sidebar toggle, identity chip (session on line 1, host on line 2), tabs, `+` and `v`, empty drag space, then bell, search and gear, then the **system** minimize, maximize and close buttons.
2. Hovering Maximize opens the Windows Snap Layouts flyout.
3. Dragging the empty space right of the tabs moves the window. Clicking any tab, the chip or an action button does not.
4. The selected tab has 8 epx rounded top corners, uses exactly the workspace layer's colour, and has no line between it and the workspace (it is connected).
5. Every tab shows a 16 epx program tile before its label. A tab with several programs shows up to 3 overlapping tiles.
6. A tab whose program needs input shows the word "Needs input" in caution colour, and its tile has a caution badge. An unseen finished tab shows "Done" in green with a check badge. Working and idle tabs show no word.
7. A tab reporting progress shows a 3 epx accent bar along its bottom. A paused report turns caution-coloured with "Paused", and a failed one critical with "Error".
8. With Mica Alt (the default), the title row and sidebar show the desktop wallpaper tint plus the aurora: pink-lilac at top-left, butter at bottom-right in light; mauve to umber in dark.
9. With Window material set to Acrylic, the blurred desktop and other windows are visible through the title row, sidebar, gaps and translucent cards, and terminal text stays legible (screenshot 3 look).
10. With Windows transparency effects off, all surfaces are opaque and cards use the Solid colours.
11. Pane cards have **12 epx corner radius**, **8 epx gaps** between them and 8 epx padding to the window's edges.
12. The focused card has a brighter hairline and a soft shadow (and a 1 epx accent border only with "Highlight the focused pane's edges" on); unfocused cards have a neutral hairline and no shadow.
13. Unfocused panes' terminal content is visibly dimmer than the focused pane's, and their card surfaces are more transparent (the aurora shows through).
14. Every pane header is 36 epx tall (30 in Compact) and shows a program tile, title, optional activity word, and on the focused pane the split right, split down, zoom, more and close buttons. Unfocused headers show their buttons only on hover.
15. Hovering a gap between panes shows a resize cursor and a small grip. Dragging resizes both panes live without any easing or animation of the terminal text.
16. Zooming a pane fills the workspace instantly, shows a "Zoomed" chip in its header, and swaps the zoom glyph for restore.
17. A single-pane tab still shows a rounded card with header, but no accent border and no dimming.
18. The terminal uses Cascadia Mono 10 pt with 12 epx side padding. Default text and background match Rex Dusk (dark) or Rex Dawn (light), and the default terminal background is see-through to the card.
19. The sidebar is on the **left**, 260 epx wide. It lists hosts with kind icons (laptop, desktop, server, penguin, cloud, globe), sessions with 20 epx program tiles and tab counts, and a collapsed "Tailnet" group. A footer holds "Add host".
20. A connected host shows no status word. A disconnected host shows "Reconnecting" (caution, with a spinner) or "Offline".
21. Opening and closing the docked sidebar slides only the sidebar. The panes jump to their new width once, without animating.
22. Ctrl+Shift+K opens a centred acrylic palette, 600 epx wide with 8 epx corners and a shadow, listing sessions grouped by host. Ctrl+Shift+P opens the same surface listing commands with keycaps.
23. Add host shows a SelectorBar ("On your tailnet" / "Enter address"), a searchable list of tailnet machines grouped as Rex servers, SSH and Offline with "Added" tags, and an informational InfoBar when Tailscale is not running.
24. A failed connection shows an Error InfoBar at the top of the workspace with the server's reason and a Retry button.
25. Closing a tab with running programs shows a ContentDialog naming the tab, host and programs. "Keep running" is the accent, focused, default button, and "Close tab" uses critical-coloured text.
26. Right-clicking a terminal opens a CommandBarFlyout with a Copy, Paste, Select all and Search icon row above Split, Zoom, Rename, Download, Clear, Reset and Close items.
27. Settings opens in its own Mica window with a WinUI TitleBar, a left NavigationView (General, Appearance, Terminal, Shortcuts, Remote hosts, Notifications, About) and SettingsCard rows with icons.
28. A "Needs input" event in an out-of-sight tab raises a Windows toast with the program's tile as logo, `{session} · {host}` attribution and an "Open" button that lands on that pane. The taskbar button flashes while the window is inactive.
29. Download file… → Save as… opens the native Windows Save dialog, and a finished download's "Show in folder" opens File Explorer with the file selected.
30. With Windows "Animation effects" off, the palette, sidebar and focus changes appear with no motion. Under any setting, no terminal text ever fades, slides or scales.

---

## 17. Open questions

1. **Name and icon.** "Windex" and any Rex mark touch Superlogical's trademarks. Confirm permission or choose a name, and design an original app icon. The Rex dinosaur artwork can't be reused (§8.1).
2. **Superlogical icon set.** If Superlogical grants a licence, ship it as a built-in icon pack through §8.6, keeping our tiles as the fallback.
3. **Local server on Windows.** Rex has no Windows server today, so "This PC" appears only through WSL or a forwarded port. Revisit if Rex ships a Windows server.
4. **Forwarding over Windows OpenSSH.** Forward a local loopback TCP port to the remote Unix socket (`-L 127.0.0.1:<port>:/path/to/server.sock`) rather than a local Unix socket, until AF_UNIX forwarding is confirmed on the bundled `ssh.exe`.
5. **Floating panes** (Rex Mac's "Float pane") are out of scope for v1. When added, they should use the same card style with `ThemeShadow` Z 32, inside the workspace layer.
