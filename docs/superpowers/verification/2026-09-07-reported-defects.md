# Reported Defects Verification

Date: 2026-09-07
Branch: `feat/avalonia-ui-migration`

Nine items reported from use. Eight are fixed and verified; one could not be reproduced.

| # | Reported | Result |
| --- | --- | --- |
| 1 | Details artwork should reach the top and carry the caption buttons | Fixed. `GameDetailView` now spans the caption row, and the strip is drawn over it. Hit-testing re-checked with artwork underneath: close, maximize and minimize still return their own codes, empty caption still returns `HTCAPTION`. |
| 2 | Sidebar list shifts on first click, scrolls sideways, needs ellipsis | Fixed. `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` on the rail list. Long names truncate, verified on "Demon Slayer -Kimetsu no Yai...". |
| 3 | Rail hover is white, should be low-opacity orange | Fixed. Hover is `#12E76C2B`, press `#1A`, selected `#2B`. The selected row measures 57,35,22 against 22,20,18 at rest. |
| 4 | Logos missing from the details page | Fixed. The hero logo is bound again and replaces the title text where a game has one. |
| 5 | Play button blur cut off at the edges | Fixed by removing it. See below. |
| 6 | Details scrollbar should be lightweight and stay after first scroll | Fixed. `AllowAutoHide="False"` plus a `persistent-scroll` class holding the bar at full opacity. |
| 7 | Left click in the rail does not highlight, only right click | Fixed. `SelectGame` now sets `SidebarSelectedItem`, so the rail marks the current game however it was opened, including from the shelf. |
| 8 | Search crashes the application | **Not reproduced.** See below. |
| 9 | Sidebar toggle jumps when the rail closes | Fixed. One button spanning both columns. Its glyph occupies x 31..45, y 15..29 in both states, byte for byte the same box. |

## The white hover and the orange selection had one cause

SimpleTheme paints hover and selection inside its own `ListBoxItem` template, where a `Background`
setter on the item cannot reach. That is where the white hover came from, and after the base
palette was retuned to Codec's colours in the previous round it became a solid orange bar instead,
which was worse. The rail list now uses an `ItemContainerTheme` whose template draws only the
item's own background, so every state belongs to the button inside the row.

This is the third time a base-theme template has leaked through a plain style, after the scroll
thumb and the toggle switch. The rule is now in `DESIGN.md`: if a control shows a colour Codec
never chose, the fix is a `ControlTheme`, not another setter.

## The play glow is gone rather than fixed

`Button` has no `BoxShadow`, so the orange bloom was a `DropShadowEffect`, and Avalonia renders an
effect within the element's own bounds. The blur was therefore squared off at the button edge.
`Border` does support `BoxShadows` and renders them outside its bounds, but reaching that would
mean wrapping every play button in the application. Given the reported result was "looks
horrible", removing it is the better trade for now. The pill still reads as primary on colour
alone.

## The search crash could not be reproduced

Four approaches, none of which crashed the process:

1. Posted `WM_CHAR` after a posted Ctrl+F.
2. Real `keybd_event` keystrokes, which failed because a background process cannot take the
   foreground, so the keys went elsewhere.
3. UI Automation `ValuePattern.SetValue` in one shot.
4. UI Automation typed incrementally through fourteen values including empties, no-match queries
   and backtracking, both with the library open and with the details page open, which is the only
   branch that behaves differently.

Approach 4 was confirmed to reach the ViewModel: a screenshot after setting "dark" shows the rail
filtered to a single result.

What was done instead:

- **A global crash log.** `Program` now records unhandled, unobserved and dispatcher exceptions to
  `crash.log` beside `settings.json`. The application is a `WinExe`, so before this a crash was a
  silent disappearance with nothing written anywhere.
- **The one genuinely fragile spot was hardened.** `RefreshSidebarFilteredGames` mutates selection
  on either side of a `Reset` on the bound collection and can loop back into itself through the
  details switch. It now runs one pass at a time.

If it happens again, `crash.log` will hold the stack trace.
