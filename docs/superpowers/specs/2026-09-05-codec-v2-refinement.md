# Codec v2 Refinement

Date: 2026-09-05
Branch: `feat/avalonia-ui-migration`
Follows: `docs/superpowers/specs/2026-08-13-codec-v2-design.md`

## Why the shipped port reads loose

The v2 implementation hit its acceptance criteria and still misses the design. The causes are
specific, and only one of them is a framework limit.

| Symptom | Cause | Framework's fault? |
| --- | --- | --- |
| Everything feels roomy and unconsidered | The reference is an 1800px frame; Codec ships 1280. Spacing and type were ported as literal pixels, so every gap is proportionally 40% larger than designed. | No |
| Scrollbar looks wrong | `master` styles scrollbars in zero lines: WinUI's stock scrollbar is already a thin auto-hiding overlay. `SimpleTheme` ships a chunky rail with arrow buttons, and the port patched its template parts instead of replacing the template. | No |
| Hover states feel generic | WinUI defined 99 `PointerOver` states across four views. The port has 24, mostly generic `Button:pointerover` rules. `Border.cover`, `Button.icon-ghost` and `Button.media-thumb` have none. | No |
| Cards lose their aspect ratio | `Height="315"` is fixed while `ItemsStretch="Fill"` stretches width, so 2:3 drifts as the window widens. | Partly. `UniformGridLayout` cannot derive height from a stretched width the way CSS `aspect-ratio` does. |
| Landscape artwork is cropped | `LibraryView.xaml.cs` went from 148 lines to 22. The wide-artwork branch that switched to `Uniform` and revealed a backdrop was deleted, not reimplemented. | No |
| Filters sit too far down | The reference's 62px top inset assumes the window controls float over content. Codec has a real 34px caption row, so it is 34 + 48 = 82. The count block was also given a second line and the tabs bottom-aligned against it. | No |

Genuine Avalonia gaps, for the record: no `backdrop-filter`, no colour-matrix filters, no text shadow,
and no aspect-ratio layout primitive. Nothing else on this list is blocked.

## Decisions

| Question | Decision |
| --- | --- |
| Metrics | Scale to Codec's window. Treat the reference as proportions, not pixels. |
| Scrollbar | Overlay, auto-hiding, reserving no layout width. |
| Landscape artwork | Letterbox on the flat card surface. No blurred backdrop. |
| Interaction | Per-control choreography: rest, hover, press, focus, disabled. |

## 1. Density pass

Two factors, because space and text do not scale together. Space is proportional to the canvas;
text has a legibility floor and scales more gently.

- **Space factor 0.72** (1280 / 1800), applied to insets, gaps and paddings.
- **Type**: display sizes scale near 0.72 because they are proportional to the canvas. Body and UI
  sizes barely move, because 13px is 13px at any canvas width.

### Spacing

| Element | Reference | Shipped | New |
| --- | --- | --- | --- |
| Rail width | 264 | 264 | 240 |
| Rail top padding | 22 | 22 | 16 |
| Rail horizontal inset | 22 | 22 | 16 |
| Rail action row inset | 18 | 18 | 13 |
| Library side inset | 44 | 44 | 32 |
| Library top inset | 62 | 48 | 12 |
| Library bottom inset | 64 | 64 | 46 |
| Header row gap | n/a | 14 | 10 |
| Header to grid | 22 | 22 | 16 |
| Grid gap | 22 | 22 | 16 |
| Cinema side inset | 68 | 68 | 48 |
| Cinema bottom inset | 56 | 56 | 40 |
| Cinema column gap | 80 | 80 | 56 |
| Cinema meta column | 320 | 320 | 240 |
| Quiet section inset | 68 | 68 | 48 |
| Onboarding side inset | 110 | 110 | 78 |

The library top inset is not a typo. The caption strip is 34px of empty, transparent space above the
content, so it already supplies most of the reference's 62. Scaled, 62 becomes 45; minus the
34px strip leaves 12.

Rail width is the one deliberate deviation. Scaled it would be 188, which truncates titles like
`DARK SOULS: REMASTERED`. 240 is the width the app used before the migration and it fits.

### Type

| Role | Reference | Shipped | New |
| --- | --- | --- | --- |
| Cinema title | 76 | 76 | 56 |
| Onboarding title | 66 | 66 | 48 |
| Onboarding subtitle | 38 | 38 | 28 |
| Screen title (scanning) | 34 | 34 | 26 |
| Empty title | 30 | 30 | 24 |
| Settings title | 26 | 26 | 21 |
| Wordmark | 21 | 21 | 18 |
| Blurb | 16.5 | 16.5 | 15 |
| Rail row | 14 | 14 | 13.5 |
| Body | 14 | 14 | 13.5 |
| Meta value | 13.5 | 13.5 | 13 |
| Factline | 13 | 13 | 12.5 |
| Caption, meta label | 12.5 | 12.5 | 12 |

`LetterSpacing` is absolute in Avalonia, so every display size needs its tracking recomputed at
`-0.015em` of the new size.

## 2. Overlay scrollbar

`SimpleTheme`'s `ScrollViewer` template exposes `PART_ContentPresenter`,
`PART_VerticalScrollBar` and `PART_HorizontalScrollBar`, and already honours `AllowAutoHide`. Two
real `ControlTheme` templates replace the current part patching.

**`ScrollViewer`**: content presenter and both scrollbars share one `Grid` cell, so a scrollbar
floats over the content instead of taking a column. `AllowAutoHide` defaults true.

**`ScrollBar`**: no line buttons, no page buttons, transparent track, thumb only.

| State | Width | Thumb |
| --- | --- | --- |
| Hidden | 0 opacity | fades out after 1s idle |
| Visible while scrolling | 8 | `#26F3EDC9`, radius 4 |
| Pointer over the bar | 10 | `#3DF3EDC9` |
| Dragging | 10 | `#52F3EDC9` |

Opacity transitions at 0.2s. The rail keeps a 5px variant under a `rail` class since the sidebar is
narrower. Fallback if the templates fight back: switch the application to `FluentTheme`, which
supplies overlay scrollbars natively, and re-tune. That is the larger change and is not the plan.

## 3. Cover integrity

### Aspect ratio

CSS gets stretched columns, fixed gaps and a locked ratio at once because `aspect-ratio` derives
height from the arranged width. `UniformGridLayout` cannot: with `ItemsStretch="Fill"` the width
stretches while `MinItemHeight` stays fixed, which is exactly the current drift.

A small `CoverGrid` behaviour on the `ItemsRepeater` reproduces the CSS result. On every bounds
change it computes:

```
columns    = max(2, floor((available + gap) / (target + gap)))
itemWidth  = (available - (columns - 1) * gap) / columns
itemHeight = itemWidth * 1.5
```

and writes `MinItemWidth` and `MinItemHeight` with `ItemsStretch="None"`. Target cover width is 210,
gap 16. At 1280 that yields 4 columns of 232 by 348, which matches the reference's own 223px cover
almost exactly. The grid fills its width, gaps stay fixed, and the ratio is exact at every size.

### Landscape artwork

`CodecImage` already decodes the bitmap and knows its `PixelSize`, so the detection that was deleted
belongs there rather than in a view. A new `LetterboxWideArt` boolean makes the control choose its
own stretch once loaded: `Uniform` when the bitmap is wider than tall, `UniformToFill` otherwise.
The card surface behind it stays flat `CodecSurfaceBrush`, per the decision above. No backdrop, no
blur, no `AcrylicBrush` substitute.

This matters in practice: `GameAssetService` falls back to `capsule_616x353.jpg` and then
`header.jpg` at 460x215 when a Steam title has no 600x900 capsule, and those currently lose roughly
their outer thirds, usually including the logo.

## 4. Interaction pass

Every interactive control gets rest, hover, press, focus and disabled defined deliberately. Motion
stays on the `0.2, 0.7, 0.2, 1` spline and stays short.

| Control | Hover | Press |
| --- | --- | --- |
| Cover card | lift 7px, art-colour ring at `.55`, glow `0 0 34 -8` | settle to 3px, glow dims |
| Library tab | underline grows from centre over 0.15s, text to full | underline holds |
| Source chip | border to `CodecPrimaryLineBrush`, fill washes in | fill deepens |
| Sort dropdown | border brightens, chevron drifts 1px down | border to primary line |
| Play pill | lift 1px, background to `#F07A37`, glow tightens | settle flat, glow off |
| Icon ghost, back | border to `CodecLineStrongBrush`, icon to full text colour | background to `#66000000` |
| Rail row | wash in, 2px bar slides in from the left over 0.14s | wash deepens |
| Rail settings | wash in, icon to full | wash deepens |
| Link row | text to primary, arrow drifts 2px right | text to pressed primary |
| Media thumb | scale 1.04, border lights to art colour | scale 1.0 |
| Favorite star | background to `#D91C1A17`, border to full accent | scale 0.94 |
| Caption buttons | wash in; close fills primary | wash deepens |
| Dropdown item | wash in | primary wash |

Focus is a 1px `CodecPrimaryLineBrush` ring at the control's own corner radius, shown only for
keyboard focus, never on pointer press. The port currently has no focus affordance anywhere.

## 5. Library header

Three corrections, independent of the density pass.

- The count returns to one line. `142 games` and `1.3 TB` join as `142 games, 1.3 TB` at
  `CodecTextMutedBrush`, which is what the reference's single `.lib-count` line does.
- Tabs and count align on their first text baseline rather than on `Bottom`, so a taller count block
  can never push the tabs down.
- The second header row renders only when `HasMultipleImportSources` is true. A library with one
  source shows tabs and count alone, and the sort control moves up beside the count.

## Scope boundaries

- No change to scanning, importing, Steam, launching, tray or update behaviour.
- No base theme swap. `SimpleTheme` stays unless the scrollbar templates prove unworkable.
- No new icon package, no new dependency.
- The pre-existing defects from the review stay out of this pass: the dead `Codec.Tests` project,
  the missing global exception handler, `package.ps1` exiting 0 without `vpk`, and the armed
  destructive confirmations surviving light dismiss. They are tracked separately.

## Acceptance criteria

1. Cover cards are 2:3 at every window width, verified at 1280 and maximised.
2. Landscape artwork is letterboxed whole on the card surface rather than cropped.
3. Scrollbars reserve no layout width, fade in on scroll and fade out when idle.
4. Every interactive control has a deliberate hover and press state, and a keyboard focus ring.
5. Library metrics match the density table, and the tabs sit 12px below the caption strip.
6. The header count is one line, and the source row is absent with a single source.
7. Build stays at zero errors and zero new warnings.
8. Verified at runtime by window capture at 1280 and maximised, not by inspection alone.
