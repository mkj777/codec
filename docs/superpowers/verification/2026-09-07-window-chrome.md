# Window Chrome Verification

Date: 2026-09-07
Branch: `feat/avalonia-ui-migration`
Plan: `docs/superpowers/plans/2026-09-07-native-vs-chromium-feel.md`, tier 1

## How this was checked

Hit-testing was verified by sending `WM_NCHITTEST` to the running window and reading the code
Windows returns. That is the same question the OS asks itself, so it is the actual behaviour
rather than a proxy for it. Placement was verified by driving `SetWindowPos`, closing the app,
relaunching, and comparing `GetWindowRect` across several cycles. Colour was measured off real
screen captures rather than `PrintWindow`, because DWM composites the frame.

## Results

### Caption hit-testing

Probed at the centre of each button, in client coordinates:

| Point | Returned |
| --- | --- |
| Close button | `HTCLOSE` |
| Maximize button | `HTMAXBUTTON` |
| Minimize button | `HTMINBUTTON` |
| Empty caption strip | `HTCAPTION` |
| Rail header, over the wordmark | `HTCAPTION` |
| Library content | `HTCLIENT` |

`HTMAXBUTTON` is the answer that matters: it is what makes Windows 11 show the Snap Layouts
flyout on hover. It is now returned, so the flyout will appear.

Maximized, probed again:

| Point | Returned |
| --- | --- |
| Close button | `HTCLOSE` |
| The single top-right corner pixel | `HTCLOSE` |
| Maximize button | `HTMAXBUTTON` |

The corner pixel is the Fitts's law case: maximized, the close button has to be reachable by
throwing the pointer at the corner of the screen without aiming. The old 14px inset made that
impossible.

Windowed, the top-right corner pixel returns `HTTOP` instead, which is correct: a resizable
window reserves its top edge for resizing.

### Placement

Window moved and sized to 300,160 at 1400x880, then closed and relaunched three times:

| Cycle | Result |
| --- | --- |
| set | 300,160 1400 x 880 |
| restart 1 | 300,160 1400 x 880 |
| restart 2 | 300,160 1400 x 880 |
| restart 3 | 300,160 1400 x 880 |

Maximize round trip: sized to 1400x880, maximized, closed, relaunched. The window reopened
maximized, and un-maximizing returned exactly 300,160 1400x880.

### Frame

Corners render rounded, confirmed from a screen capture of the top-left corner against the
desktop. The border colour is set to `#181D20` in DWM's channel order, which is invisible
against a dark desktop and is meant to be.

## Corrections made during implementation

- **The window grew by 18x10 pixels on every launch.** Placement was being saved from
  `FrameSize` but restored into `Width` and `Height`, and in Avalonia those set the *client*
  area. Every cycle added the frame inset. Measured across three restarts as 1400, 1418, 1436,
  1454. Now saves `ClientSize`, which is symmetric with what it restores.
- **Maximizing overwrote the size the user had chosen.** `ClientSize` changes before
  `WindowState` does, so an inline read recorded the maximized rectangle as the normal one. The
  capture is now deferred one dispatcher pass, by which point the state has settled, and is
  skipped unless the window is genuinely normal.
- **The rail header was not draggable.** The `TitleBar` role was set on it but the grid had no
  background, so it was invisible to hit-testing and the probe returned `HTCLIENT`. Giving it a
  transparent background made it `HTCAPTION`.
- **`BeginMoveDrag` and the double-click handler were deleted.** With a `TitleBar` role the OS
  owns caption behaviour, including double-click to maximize and the right-click system menu.

## Mica was tried and rejected

Measured, not assumed. With `TransparencyLevelHint="Mica"` and a transparent window background:

| Region | Colour | Warmth (R minus B) |
| --- | --- | --- |
| Caption strip | 32,32,32 | 0 |
| Rail | 25,25,25 | 0 |
| Shelf (opaque, unaffected) | 28,26,23 | 5 |

Mica replaced Codec's warm base with neutral grey in exactly the two regions that carry the
brand, and split the window into a cool top-left against a warm content area.

Adding a warm tint over it fixed the colour and removed the point:

| Region | Colour with an 85 percent `#1C1A17` tint | Warmth |
| --- | --- | --- |
| Caption strip | 29,27,25 | 4 |
| Rail | 23,21,20 | 3 |
| Shelf | 28,26,23 | 5 |

At a tint strong enough to keep Codec's identity, Mica contributes about one value point and is
invisible. At a tint weak enough for Mica to read, it destroys the palette. There is no useful
middle for a dark, saturated, self-contained design, and none of the applications this work is
chasing use it: Claude, Steam, Spotify and Vortex are all flat and opaque. Reverted.

## Known gaps

- **The Snap Layouts flyout itself was not seen.** It requires a real pointer hovering the
  button; the OS does not raise it for synthetic messages, and the real cursor was not moved.
  What is verified is the precondition, which is that Windows classifies the button as
  `HTMAXBUTTON`.
- **Corner rounding could not be attributed.** DWM rounds this window shape by default, so
  setting `DWMWA_WINDOW_CORNER_PREFERENCE` explicitly is correct and guarantees the behaviour,
  but the attribute could not be shown to be the deciding factor.
- **Caption button hover was not driven,** for the same reason every hover in the previous round
  could not be: a posted `WM_MOUSEMOVE` is cancelled by `WM_MOUSELEAVE`. The red close hover is
  a declarative rule.
