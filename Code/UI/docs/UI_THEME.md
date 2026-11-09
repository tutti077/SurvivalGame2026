# UI theme — how every panel gets its look

Mark 2026-11-08: the UI was "grey boxes made in MS Paint"; it now has one theme behind the whole UI,
swappable later (new background, new palette) without touching panel code. Nothing in
`Code/UI/**` names a colour, font, corner radius, border width or texture of its own.

## Pieces

| File | Role |
|------|------|
| `Assets/data/ui_themes.json` | The themes. `defaultTheme` + a list; each has `fonts`, `colors`, `metrics`, `textures`. Colours are `#rrggbb[aa]` or `r,g,b[,a]`. Sizes are 1080p design pixels. |
| `Code/UI/Theme/UiTheme.cs` | Loads the catalog, holds `UiTheme.Current` (+ `Colors` / `Metrics` / `Fonts` / `Textures` shortcuts), `Changed` event, `Version`, `ui_theme` console command, per-machine choice in `FileSystem.Data/ui_theme.json`. |
| `Code/UI/Theme/UiStyle.cs` | Inline-style helpers for code-built panels: `Surface`, `Text`, `Button` / `SetButtonState`, `Slot` / `SetSlotState`, `Bar`, `KeyCap`, `Tab`, `ScrollBar`, `SoftCursor`, `Field`, `Divider`, `AddTitle`, `Backdrop`, `AddCornerOrnaments`. |
| `Code/UI/Theme/UiThemeStyleSheet.cs` | The same theme as a generated stylesheet (`ui-card`, `ui-title`, `ui-button:hover` …) for razor panels — `UiThemeStyleSheet.Attach( Panel )` once per root, re-attached on `UiTheme.Changed`. |
| `Tools/ui_theme/generate_ui_theme_textures.py` | Writes `Assets/ui/theme/<id>/panel_tile.png`, `corner.png`, `vignette.png`, `divider.png` (white-on-alpha, tinted at runtime; the vignette is black). |

Themes shipped: **ember** (default — charcoal, warm off-white text, brass accent `#d9a441`, the
hotbar's old gold) and **frost** (steel + ice) as the proof that a swap is a JSON edit.

## Rules for panel code

- **Surfaces** go through `UiStyle.Surface( panel, UiSurface.X )`: `Panel` (menu column / page —
  grain tile + shadow), `Frame` (hero panel — strong accent border + corner ornaments), `Raised`
  (card inside a panel), `Sunken` (list well, grid background), `Pill` (HUD readout), `Tooltip`.
  Padding: `UiStyle.Padding( panel, scale )`. Layout (position, size, flex, gap) stays in the
  owning class — the theme never moves anything.
- **Text** goes through `UiStyle.Text( label, UiText.X, size? )` / `AddLabel` / `AddTitle`:
  `Title` (page title, heading font, uppercase + letter spacing per theme), `Header` (sub-heading,
  accent), `Body`, `Secondary`, `Muted`, `Value` (numbers), `Accent`, `Hud` (floating text with the
  theme shadow). Sizes default to the theme metrics (body ≈ 17, header ≈ 20, title ≈ 28, small
  ≈ 13); a section that scales its text passes `UiTheme.Metrics.FontBody * Scale`.
- **Buttons**: `UiStyle.Button( panel, UiButton.Default | Primary | Danger | Ghost )` at build,
  `SetButtonState( panel, kind, hovered, pressed, disabled )` from the existing hover / press
  polling, `ButtonLabel( label, kind )` for the caption. One primary per page.
- **Slots** (inventory, hotbar, binds, build tiles, sockets): `UiStyle.Slot( panel )` then
  `SetSlotState( panel, UiSlotState.Idle | Hover | Selected | Preferred | Locked | Pending | Active | Danger )`.
  Count badges: `CountBadge( label )`.
- **Bars**: `UiStyle.Bar( track, fill, UiTheme.Colors.HealthColor, UiTheme.Colors.HealthDepletedColor )`,
  `AddBarGloss( fill )`, `BarLabel( label )`. Boss / enemy bars take their fill colour from their own
  data (bosses.json) and the track / border from the theme.
- **Semantic colours** come from `UiTheme.Colors`: `PositiveColor`, `NegativeColor`, `WarningColor`,
  `InfoColor`, `AccentColor`, `TextSecondaryColor` … Data-driven colours (resource fallback tints,
  cable colours, map pin colours, hoe tile swatches, crew colours) stay data — they are content,
  not chrome.
- **Sizes** that other files reference as `const` (slot sizes, section scales, row heights used
  by click arithmetic) stay `const`; the theme owns look, not layout arithmetic.
- **Scaling**: every HUD / menu root sits under a `ScreenPanel` with `AutoScreenScale` on, so
  design pixels are 1080p-relative and follow the window / fullscreen height. Use `%` / `vh` for
  anything that should grow with the window (the crafting section height already does); keep
  fixed pixels only for things sized by their content (a 4-column slot grid).
- **Bottom HUD strip**: the vitals (buff row, bars, food slots) stay visible over the menu, so the
  menu side anchors end `PlayerScreenHud.MenuColumnBottomInset` above the screen bottom (and start
  `MenuColumnTopInset` below the top). A section that fills its column derives its height from those
  constants (`CraftingMenuSection.SectionViewHeightPercent`) — never from a bare vh number.
- **Hover** on soft-cursor menus is polled (`InventoryScreenPointer.PanelBoxContainsScreen`) — map
  the poll result to `SetButtonState` / `SetSlotState`; CSS `:hover` only works where the OS cursor
  is live (main menu, load screen, build picker).

## Adding a theme

Copy a theme block in `ui_themes.json`, change `id` / `displayName` / colours / textures, run the
texture generator (add the id to `THEMES` there for a tile variant, or point `textures` at hand-made
art), then `ui_theme <id>`. Menus built before the switch restyle on their next build (scene load);
the main menu and load screen restyle live.

## Verification

Open every page of the game menu (Tab / C / M / Esc pages, Augments + Skills + Quests tabs), the
build / hoe / wire pickers, the time trial and arena menus, the main menu and the load screen, in a
windowed editor viewport and fullscreen. Nothing should clip, overlap or keep a grey `#383d47` box.

From the editor MCP (no input simulation): `run_csharp` finds the local
`PlayerGameMenuController` and calls `SetActivePage( MenuPageIds.X )`, `ToolBuildHammer.SetBuildMenuOpen( true )`
opens the build picker after `equip build_hammer`, `GameMainMenu.Page = …; StateHasChanged()` flips the
main menu pages (a frame later), and `camera_screenshot` at 1280×720 / 1920×1080 / 2560×1440 with UI
shows the same layout at every size — that is the windowed-vs-fullscreen check.

Checked 2026-11-09 (build 0.8.3083): HUD, inventory + equipment, crafting, quests, skills, map, settings,
augments page, build picker, main menu (main + load pages), three render sizes. Not opened: hoe / wire
pickers, time trial + arena menus, grapple prompt, augment station, boss / raid banners, the load screen
(it only shows while a world streams in).
