# Avalonia Shell Refinement Design

Date: 2026-08-11  
Branch: `feat/avalonia-ui-migration`

## Goal

Bring the Avalonia shell closer to the approved Codec v2 references by correcting the sidebar hierarchy, spacing, typography, borders, caption controls, and icon language. Library and Game Details composition remain unchanged.

## Icon system

Use `Lucide.Avalonia` 0.1.64 throughout the visible Avalonia UI. This version targets .NET 8 or higher and is compatible with the project's .NET 9 and Avalonia 12 stack.

- Standard icon size: 16 DIPs.
- Sidebar toggle: 18 DIPs.
- Standard stroke width: 1.75 DIPs.
- Replace improvised Unicode action glyphs with their Lucide equivalents.
- Keep icons outline-only. State is communicated with color, not mixed filled and outlined icon families.
- Do not expose Lucide types through models, core services, or presentation state. Icons remain a view concern.

## Sidebar layout

The sidebar is open by default and fully collapses to zero width.

- Open width: 240 DIPs.
- Collapsed width: 0 DIPs; no residual icon rail remains.
- When collapsed, a quiet 34 by 34 DIP `PanelLeftOpen` button appears at the upper-left of the content surface.
- The open sidebar contains a matching `PanelLeftClose` button in its brand row.
- The sidebar uses 20 DIP horizontal insets and a restrained right divider.
- Content order is: brand, search, game list, flexible rest area, mascot, Add and Scan, Settings.
- The mascot sits above the action buttons and does not compete with the empty-library illustration.
- Settings is a separate bottom row with a subtle top divider.

## Sidebar controls

- Brand: Nunito, 18 DIPs, weight 700.
- Search: transparent surface, 16 DIP `Search` icon, 13 DIP text, weight 400, muted foreground, and only a bottom border.
- Game entries: 13 DIPs, weight 500, compact 34 DIP target height, no card-like border.
- Add and Scan: 34 DIP height, 8 DIP corner radius, 13 DIP text, weight 600, 16 DIP icons.
- Add remains the single orange primary action. Scan remains a quiet secondary action.
- Settings: 40 DIP row, 13 DIP text, weight 500, 16 DIP `Settings` icon.

## Color and border calibration

The AGENTS.md brand palette remains authoritative.

- Main background: `#1C1A17`.
- Sidebar: `#141210`, darker than the canvas without becoming pure black.
- Surface: `#24211E`.
- Hover surface: `#2B2722`.
- Border: `#332E28`, used only for the sidebar divider, search underline, settings separator, and interactive outlines.
- Primary: `#E76C2B`.
- Text: `#F3EDC9`.
- Muted text: `#9E8E78`.

Sidebar controls avoid filled rounded containers unless they are real buttons. Search and Settings remain visually quiet.

## Window caption

Remove only Avalonia's fullscreen caption action. Keep Minimize, Maximize or Restore, and Close. Preserve dragging and double-click maximize behavior. The caption surface must use the same quiet background as the application and must not add another toolbar.

## Scope boundaries

- Do not redesign LibraryView or GameDetailView composition.
- Do not add cover captions or a Continue hero.
- Do not change search, navigation, scan, settings, window lifecycle, or tray behavior.
- Do not introduce a second icon package or hand-maintained SVG collection.
- Do not add or run tests. Validate with a fresh isolated x64 build and a runtime screenshot comparison.

## Acceptance criteria

- The sidebar opens at launch and collapses completely.
- A visible content-side control reopens a collapsed sidebar.
- Sidebar content order and spacing match the approved v2 reference hierarchy.
- No fullscreen caption button is visible.
- Visible action glyphs use Lucide consistently.
- Typography, colors, button dimensions, borders, and rounding follow the values above.
- The Avalonia project builds with zero errors.
