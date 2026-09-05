# Codec v2 Design

Date: 2026-08-13
Branch: `feat/avalonia-ui-migration`

## Goal

Bring the Avalonia UI to the approved Codec v2 reference (`Codec v2.html`, Claude Design project
`18b0b442-0c48-4e9e-8969-3b147dc04823`). The migration reached feature parity but not design parity:
layout metrics, dropdowns, scrollbars, and progress indicators still show Avalonia `SimpleTheme`
defaults, and the mascot appears in the sidebar even when the library is empty.

The reference is a 1920x1080 stage holding an 1800x936 window frame. Codec ships a 1280x760 window.
Spacing and type sizes port literally; anything the reference expresses as a fixed column count is
re-expressed as a reflowing minimum.

## Source of truth

`Codec v2.html` carries the stylesheet. Component structure lives in `v2/core.jsx`, `v2/screens.jsx`,
`v2/details.jsx`, `v2/app.jsx`, `v2/covers.jsx`. Where this spec and the reference disagree, this
spec wins. It reconciles the reference against features Codec has and the reference does not.

## Decisions taken against the reference

| Reference behavior | Codec decision | Reason |
| --- | --- | --- |
| Title under every cover | No titles | `DESIGN.md` line 20 |
| 250px Continue hero above the grid | No hero | `DESIGN.md` line 20 |
| Library header is 3 tabs only | Tabs plus sort dropdown plus source chips | Codec has filters the reference never saw |
| Game Details offers editorial / gallery / ambient | Ambient, cinema mood | Selected |
| Notice carries no numbers | Percentage retained | `PRODUCT.md` line 11 |

## 1. Foundations

### Typography

Both faces are already bundled under `Assets/fonts`.

| Resource | Family | Applies to |
| --- | --- | --- |
| `CodecDisplayFontFamily` | Sansation, weight 700, `LetterSpacing` `-0.015em` | h1, h2, wordmark, Play pill |
| `CodecUiFontFamily` | Nunito | all other text |

Sizes: page title 76 (cinema) / 66 (onboarding step 1) / 34 (scanning) / 30 (empty state),
wordmark 21, blurb 16.5 with line height 1.68, body 14, factline 13, caption and meta label 12.5,
meta value 13.5.

`Typography.axaml` currently maps all four weight resources to Nunito. Replace with the two
resources above and retire `CodecRegularFontFamily`, `CodecMediumFontFamily`,
`CodecSemiBoldFontFamily`, `CodecBoldFontFamily`.

### Color

The palette in `AGENTS.md` stays authoritative. Separation moves from the solid `#332E28` border to
alpha lines that read against whatever sits beneath them.

| Resource | Value | Use |
| --- | --- | --- |
| `CodecLineBrush` | `#17F3EDC9` | frame border, ghost buttons, icon-ghost |
| `CodecLine2Brush` | `#0EF3EDC9` | rail divider, search underline, meta rows, link rows |
| `CodecSinkBrush` | `#38000000` | rail background |
| `CodecHoverWashBrush` | `#09F3EDC9` | list and chip hover |
| `CodecLineStrongBrush` | `#2EF3EDC9` | hovered ghost border |
| `CodecPrimaryWashBrush` | `#1AE76C2B` | selected list row, checked chip |
| `CodecPrimaryLineBrush` | `#52E76C2B` | Add button border, checked chip border |
| `CodecOnArtLineBrush` | `#29F3EDC9` | meta rows and link rows drawn over artwork |

`CodecSidebarColor` `#141210` is retired as a rail background; the rail becomes `CodecSinkBrush`
over `CodecBackgroundBrush`. `#141210` survives only as the notice pill base (`#F0141210`).
`CodecBorderBrush` is kept as an alias of `CodecLineBrush` so existing references keep compiling
until they are migrated.

### Motion

The reference easing `cubic-bezier(.2,.7,.2,1)` maps to `SplineEasing` with `X1=0.2 Y1=0.7 X2=0.2
Y2=1`. A new `Styles/Motion.axaml` holds the shared durations and the four animations.

| Name | Definition | Applied to |
| --- | --- | --- |
| `rise` | opacity 0 to 1, `TranslateY` 14 to 0, 0.35s | notice pill, onboarding step |
| `fade` | opacity 0 to 1, 0.3s | disclosure body |
| `sweep` | `TranslateX` -120% to 320%, 1.7s, `SplineEasing 0.5,0,0.5,1`, infinite | indeterminate hairline |
| `breathe` | opacity 0.35 to 1 to 0.35, 1.5s, ease-in-out, infinite | busy notice dot |

Transition durations: 0.14s list rows, 0.15s ghost and icon buttons, 0.18s Play pill, 0.2s
disclosure chevron, 0.3s cover cards.

### Art tint

`ArtColorService` ports `covers.jsx:useArtColor`. Given a cover source it decodes the bitmap,
scales to 24x36, and averages every pixel where saturation exceeds 0.22 and luminance falls between
45 and 225. Fewer than 24 qualifying pixels falls back to a plain average of all pixels. A result
below luminance 90 is scaled up to 90. Results are cached by source string; decoding happens off the
UI thread and the service returns the fallback `#E76C2B` until the real value arrives.

The tint drives the cover hover ring and glow, and the cinema radial veil. It is a view concern and
must not appear on models or in `Codec.Core`.

## 2. New controls

### `Hairline`

A `TemplatedControl` replacing every `ProgressBar` in the application.

- Track: 1px tall, `CodecLineBrush`, full width of its slot.
- Determinate: filled from the left to `Value / Maximum` in `CodecPrimaryBrush`.
- Indeterminate: a bar 40% of track width carrying a
  `transparent -> CodecPrimaryBrush -> transparent` horizontal gradient, running the `sweep`
  animation.
- `IsComplete`: the whole track becomes solid `CodecPrimaryBrush` and the sweep stops.

The indeterminate `ProgressBar` currently nested inside the Play button (`GameDetailView.axaml` line
71) is removed. While a launch is in flight the play row instead shows a 120px indeterminate
`Hairline` directly beneath it, and the Play pill keeps its glyph rather than swapping it for a
spinner.

### `ControlThemes.axaml`

`App.axaml` keeps `SimpleTheme`. `SimpleTheme` supplies raw templates whose geometry cannot be
corrected by property setters, which is why the current `Controls.axaml` styling does not reach the
dropdown popup, the scrollbar rail, or the progress bar shape. This file supplies real
`ControlTheme` templates for the three affected controls.

**`ScrollBar`**: no line or repeat buttons, transparent track, thumb `#14F3EDC9` with
`CornerRadius` 4 and a 32px minimum length. Width 8, or 5 under the `rail` class. Auto-hide with an
opacity transition; visible on pointer over.

**`ComboBox`**: closed box `CornerRadius` 10, `CodecSurfaceBrush`, 1px `CodecLineBrush`, minimum
height 36, padding 12,7, `ChevronDown` at 14 DIPs in muted. Popup `CornerRadius` 10,
`CodecSurfaceBrush`, 1px `CodecLineBrush`, padding 6, `BoxShadow` `0 20 50 0 #80000000`.
`ComboBoxItem` `CornerRadius` 7, padding 9,7, 13px, muted; hover `CodecHoverWashBrush` and text
foreground; selected `CodecPrimaryWashBrush` and text foreground.

**`ProgressBar`**: retained only so any missed usage inherits the hairline look: 1px height, no
corner radius, primary on `CodecLineBrush`.

### `CodecImage.VerticalBias`

A `double` styled property, default 0.5, feeding `CalculateDestination` so `UniformToFill` can
anchor the crop away from center. `background-position: center 22%` becomes `VerticalBias="0.22"`.
Horizontal centering is unchanged. Existing call sites keep the default and are unaffected.

## 3. Shell

Rail width 264 open, 0 collapsed. Background `CodecSinkBrush`, right divider 1px `CodecLine2Brush`.
Top padding 22.

Order and metrics, top to bottom:

1. **Brand row**: padding 0,22,18. Wordmark `Codec`, display face, 21px. `PanelLeftClose` at 15
   DIPs, muted, opacity 0.6, right-aligned.
2. **Search**: visible only when `HasGames`. Margin 0,22,8, padding bottom 10, bottom border 1px
   `CodecLine2Brush`. `Search` icon 14 DIPs muted, 10px gap, 14px input, placeholder muted at 0.75.
3. **Game list**: fills remaining height, padding 12,12,12,10, 1px between rows. Row padding 7,12,
   `CornerRadius` 8, 14px, muted, single line with ellipsis. Hover: `CodecHoverWashBrush` plus text
   foreground. Selected: text foreground, `CodecPrimaryWashBrush`, and a 2px `CodecPrimaryBrush` bar
   at left inset 3, `CornerRadius` 2, inset 9 from top and bottom. Transition 0.14s.
4. **Empty list line**: when the list renders nothing, a 13px muted row at opacity 0.7 reading
   `No games yet` when the library is empty and `Nothing found` when a search excludes everything.
   Padding 10,12.
5. **Mascot**: `IsVisible` bound to `HasGames`. This is the defect being fixed: the mascot must not
   appear beside the empty-library illustration. Height 116, margin 2,18,6, uniform stretch.
6. **Actions**: `Add` and `Scan` side by side, equal width, 8px gap, padding 10,18,12. Both are
   ghost buttons: transparent background, 1px border, `CornerRadius` 9, padding 9,10, 13px weight
   600, icon 13 DIPs, 7px gap. `Add` uses `CodecPrimaryBrush` foreground and `CodecPrimaryLineBrush`
   border; hover raises the border to `#99E76C2B` over a `#12E76C2B` wash. `Scan` is muted with
   `CodecLineBrush`; hover raises to `CodecLineStrongBrush` over `CodecHoverWashBrush`.

   `Add` stops being a filled orange button. Orange remains reserved for the primary next action per
   `DESIGN.md` line 27, and in v2 the rail expresses that with color rather than fill.
7. **Settings**: full-width row, padding 13,22, top border 1px `CodecLine2Brush`, 13px muted with a
   15 DIP `Settings` icon at 9px gap. Hover moves foreground to text.

Window caption keeps its 34px height and current behavior. Buttons become 30x26, `CornerRadius` 7,
muted at opacity 0.7; hover `#0FF3EDC9` with text foreground; close hover fills `CodecPrimaryBrush`
with a `#1C1A17` glyph.

## 4. Library

### Header

Two rows, always rendered in this arrangement, so controls do not move when a source appears or
disappears.

```
All  Installed  Recent                                  142 games
---                                                     18.4 TB

[All sources] [Steam] [Epic] [Riot]           [Alphabetic v]
```

Outer padding 48,44,64,44. Header rows 14 apart, 22 below the header before the grid. The reference
uses 62 top padding for a single header row; two rows require the reduction.

**Tabs**: `All`, `Installed`, `Recent`. 14px, muted, padding bottom 4, 22px apart, transparent 1px
bottom border. Hover moves foreground to text. Selected takes text foreground and a
`CodecPrimaryBrush` bottom border. Transition 0.15s.

`Recent` is undefined in the reference beyond a non-empty last-played value. Codec defines it as the
12 games with the most recent `Game.LastPlayedUtc`, falling back to `Game.DateAdded` where that is
null. `LastPlayedUtc` is only populated for Steam-owned games, so on a library without a Steam
connection `Recent` degrades to the 12 most recently added games.

**Count**: right aligned, 12.5px muted at opacity 0.8, with total library size beneath as caption.

**Source chips**: the reference has no chip primitive, so chips inherit `.ghost` geometry, giving
the application one button language across rail and library.

| State | Border | Fill | Foreground |
| --- | --- | --- | --- |
| rest | `CodecLineBrush` | transparent | muted |
| hover | `CodecLineStrongBrush` | `CodecHoverWashBrush` | text |
| checked | `CodecPrimaryLineBrush` | `CodecPrimaryWashBrush` | text |

`CornerRadius` 9, padding 9,10, 13px weight 600, 8px apart. `All sources` uses the same shape and
clears the selection.

**Sort**: the themed `ComboBox` from section 2, retaining all six current options and the existing
`SelectedSortIndex` binding.

The install-filter `ComboBox` is removed. The `Installed` tab performs the same filter, and keeping
both would be two controls for one decision. `SelectedInstallFilter` is retired from the view model
along with `InstallFilterDisplay`; the `HasNotInstalledLibraryGames` guard that forces the filter
back to `1` goes with it.

### Grid

No cover titles and no Continue hero, per `DESIGN.md` line 20.

The reference fixes 6 columns inside an 1800px frame. At Codec's 1280px window that yields 123px
covers, so the fixed count is replaced by `UniformGridLayout` with `MinItemWidth` 210,
`MinItemHeight` 315, and 22 minimum row and column spacing, `ItemsStretch` `Fill`. This reaches the
reference's ~228px covers on a wide window and reflows below it.

Cards: aspect 2:3, `CornerRadius` 10, `BoxShadow` `0 10 26 0 #5C000000`. Hover applies
`TranslateY` -7, `BoxShadow` `0 20 44 0 #80000000`, a 1px border in the art tint at alpha `#8C`, and
a glow `0 0 34 -8` in the art tint at alpha `#80`. Transition 0.3s on the spline easing.

The favorite star and the folder-size and date-added tags have no reference equivalent because the
reference has no such features. They stay, with tag `CornerRadius` 7, `#D91C1A17` background,
padding 8,4.

### Empty and loading states

Empty: mascot 250x180, then `Nothing here yet.` at 30px display face, then a Play pill reading
`Add games` beside a text button reading `Scan a folder`, 24px apart, centered.

Initial loading: a 260px `Hairline` above a single line of text. No counts in the subtitle.

## 5. Game Details: ambient, cinema mood

### First viewport

```
+----------------------------------------------------------+
|  (<-)                                                     |
|         cover art, sharp, UniformToFill, VerticalBias .22 |
|                                                           |
|...........................................................|
|== Steam - Action RPG - 2022                               |
|==                                         Developer  From |
|== ELDEN RING                     76px     Released   2022 |
|==                                         Main story  58h |
|== Rise, Tarnished. A vast land...         More v          |
|== ( > Play )  (gear)                                      |
+----------------------------------------------------------+
   |- 68 -|        max 720        | gap 80 |  320  |  56 up
```

The mood keeps the artwork sharp; legibility comes from directional scrims and text shadows rather
than blur. The reference's `saturate(1.06) contrast(1.03)` has no Avalonia equivalent and is dropped
as visually negligible.

**Artwork**: `CodecImage`, `UniformToFill`, `VerticalBias` 0.22, `RenderTransform` scale 1.02,
`ClipToBounds`. The reference samples the portrait capsule; Codec prefers `LibraryHero` where
present and falls back to `LibraryCapsule`.

**Veils**: three stacked `Border` layers over the artwork. Base color is `#181613`, not
`CodecBackgroundColor`.

1. Bottom scrim, `LinearGradientBrush` from `0%,100%` to `0%,0%`: `#FB181613` at 0,
   `#F0181613` at 0.17, `#A8181613` at 0.36, `#33181613` at 0.57, `#00181613` at 0.76.
2. Top scrim, `LinearGradientBrush` from `0%,0%` to `0%,100%`: `#E0181613` at 0,
   `#59181613` at 0.22, `#00181613` at 0.38.
3. Art radial, `RadialGradientBrush` centered `50%,106%`, `RadiusX` 1.0, `RadiusY` 0.7: art tint at
   alpha `#42` to transparent at 0.66.

**Back button**: 38px circle at margin 26,22, `#4D000000` background, 1px `CodecOnArtLineBrush`,
text foreground, above the veils. The reference's `backdrop-filter: blur(10px)` has no Avalonia
equivalent; the flat translucent fill replaces it.

**Content**: bottom aligned, padding 0,68,56,68, two columns 80px apart.

Left column, maximum width 720, 22px between items:

- Factline: source, primary genre, release year, separated by 3px dots at opacity 0.5. 13px muted,
  `LetterSpacing` 0.02em. The reference's third fact is playtime; Codec tracks no playtime on
  `Game` or anywhere in `Codec.Core`, so release year takes that slot. Any missing fact is omitted
  together with its separating dot.
- Title: 76px display face, line height 1, `TextShadow` `0 10 44 #9E000000`.
- Blurb: 16.5px, line height 1.68, `#E6F3EDC9`, maximum width 620, `TextShadow` `0 2 16 #99000000`.
- Play row: Play pill, then a 44px circular ghost for game settings, then `Not installed` as a
  12.5px muted note at opacity 0.8 when applicable. 14px apart.

Play pill: `CodecPrimaryBrush`, foreground `#221E19`, `CornerRadius` 999, display face weight 700,
`LetterSpacing` 0.04em, padding 15,34, 15.5px, `BoxShadow` `0 10 28 -10 #BFE76C2B`. Hover
`#F07A37` with `TranslateY` -1. Disabled drops to opacity 0.4 with no shadow.

Right column, width 320, bottom padding 4: meta rows for Developer, Released, Main story, then a
`More` disclosure holding Publisher, Age rating, Price, Completionist and the external links.

Meta row: label 12.5px muted left, value 13.5px text right, padding 11 vertical, 1px top border. Over
artwork the border is `CodecOnArtLineBrush` and values carry `TextShadow` `0 1 12 #BF000000`; labels
carry `0 1 10 #BF000000`.

Disclosure: 12.5px muted button with a chevron that rotates 180 degrees over 0.2s; body animates in
with `fade`.

### Second section

Cinema holds four facts. Codec's details view carries roughly twenty, and `DESIGN.md` line 21
requires the existing information order be preserved. Cinema therefore occupies the first viewport
and the page scrolls into a quiet second section on `CodecBackgroundBrush` at matching 68px side
insets, carrying the remainder in the reference's flat row language:

1. Summary metrics (Steam reviews, main story, completionist, release date, controller) as meta
   rows rather than the current five-column card.
2. Metadata and description.
3. Taxonomy (genres, themes, game modes) as chips using the source-chip rest style.
4. Steam achievements, platform logos, franchise entry.
5. Media strip and external links.

Every `Border.detail-surface` card dissolves into hairline-separated rows. This also satisfies
`DESIGN.md` line 22, which asks that nested cards be avoided.

The game settings drawer, media overlay, and franchise overlay keep their current behavior and
receive the same flat-row restyle.

## 6. Notice and progress

One pill, bottom center, margin 26 from the bottom. `CornerRadius` 999, `#F0141210` background, 1px
`CodecLineBrush`, padding 11,20,11,16, 13.5px text, `BoxShadow` `0 18 44 0 #80000000`, entering with
`rise`. A 7px dot leads: `CodecAccentBrush` running `breathe` while work is in flight,
`CodecPrimaryBrush` when finished. Non-busy notices dismiss after 4.2 seconds.

One notice is visible at a time; the most recent wins and others queue. This replaces the
bottom-right `NotificationStack`.

A `Hairline` sits directly beneath the pill. Where a real percentage exists it runs determinate;
otherwise it sweeps and the pill states no quantity.

Copy carries the percentage, never internal stages or raw counts:

| Current | Becomes |
| --- | --- |
| `Loading covers (12/40)` | `Loading covers - 30%` |
| `Preparing artwork... (12/40)` | removed |
| `Fetching Covers...` | `Loading covers` |

This keeps `PRODUCT.md` line 11 satisfied, so `PRODUCT.md` needs no revision.

The blocking scan overlay in `MainWindow.axaml` lines 191 to 202 is removed; background work never
blocks the library. The first run instead shows the full scanning screen: centered, 34px display
heading `Finding your games`, a 260px determinate `Hairline`, and `This takes a moment.` beneath.
On completion the heading becomes `Your shelf is ready.`, the hairline goes solid, and a Play pill
reading `Open library` replaces the caption.

## 7. Onboarding and Settings

Onboarding keeps three steps inside padding 0,110. Step 1: 66px display headline over an 17px muted
paragraph at maximum width 460, then a Play pill and a text button. Step 2: source rows 16px with
1px `CodecLine2Brush` top borders, last row also bottom-bordered, selected rows taking text
foreground with an 18px primary tick. Step 3: confirmation and a Play pill. The wordmark sits at
56,42. Dots sit at 110 from the left and 56 from the bottom: 5px circles, muted at opacity 0.35, the
active one an 18px wide primary pill with 3px radius over a 0.3s transition. The mascot occupies a
400x300 slot at the right, vertically centered, on step 1 only.

Settings keeps its current content and overlay behavior. Its nested `detail-surface` blocks become
meta rows and hairline-separated groups, matching section 5.

## Scope boundaries

- No changes to scanning, importing, Steam, launching, tray, window lifecycle, or update behavior
  beyond the copy changes listed in section 6.
- No second icon package. Lucide stays.
- No changes to `Codec.Core` or to models.
- No new tests. Validation is a clean x64 build plus a runtime comparison against the reference.
- The gallery, editorial, clear, pane, and aura variants are not implemented.

## Acceptance criteria

1. The sidebar mascot is hidden whenever the library is empty.
2. Dropdown popups, scrollbars, and progress indicators no longer show `SimpleTheme` defaults.
3. Every `ProgressBar` in the application is replaced by `Hairline`.
4. Sort, source chips, and install filtering all remain reachable; sort keeps its six options and
   source chips keep per-source toggling.
5. Cover cards carry no titles and the grid carries no Continue hero.
6. Game Details renders the ambient cinema mood over sharp artwork, and no information present in
   the current details view is lost.
7. Progress copy states an activity and a percentage, never a raw count or an internal stage.
8. Display type is Sansation; UI type is Nunito.
9. The Avalonia project builds with zero errors and zero new warnings.
