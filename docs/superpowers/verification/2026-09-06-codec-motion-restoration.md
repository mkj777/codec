# Codec Motion Restoration Verification

Date: 2026-09-06
Branch: `feat/avalonia-ui-migration`
Spec: `docs/superpowers/specs/2026-09-06-codec-motion-restoration.md`

## How this was checked

Clean x64 Debug build, then the running window captured with `PrintWindow` at 120 DPI with the
capture process marked per-monitor DPI aware, so the bitmap matches the window's real pixels.
Interaction was driven by posting pointer messages to the window rather than by moving the real
cursor.

Timed states were captured by taking several frames at known offsets after an action and
measuring individual pixels, which is what makes "it animates" a measurement rather than an
impression.

## Acceptance criteria

| # | Criterion | Result |
| --- | --- | --- |
| 1 | The launch sequence plays and covers the load | Verified. Frames at 350ms (first row of tiles up), 1300ms (all nine, real covers), 2000ms (mosaic contracted to 0.84 and settled to 0.28 with the wordmark through it), 3600ms (library). |
| 2 | A failed load still dismisses the overlay | By construction: the wait is inside `try`/`catch` and `IsVisible = false` follows it unconditionally. Not exercised, the load did not fail. |
| 3 | Opening and closing Settings twice animates twice | Verified. First open at 80ms: covers still readable through a half-drawn scrim. Close at 90ms: the cover behind it measured 348 against 143 when open and 373 once gone. Second open in the same session at 80ms measured 163, so the entrance ran again. |
| 4 | Library and details crossfade rather than cut | Verified. A cover pixel measured 170 at 70ms after Back and 195 at 160ms and after. |
| 5 | The rail collapses over time | Verified. 70ms after the toggle the rail was mid-width with its content ghosted and the shelf already reflowed to five columns; by 470ms it was gone. |
| 6 | Pressing a cover moves the card | Not verified. See below. |
| 7 | Clicking Play pulses | Not verified. See below. |
| 8 | No interaction rule changes an untransitioned property | Verified by inspection of every `:pointerover`, `:pressed` and `:focus-visible` rule against the transition set its control inherits. `Button.play` and `Button.favorite` were re-declaring `Transitions` and silently dropping properties from the base set; both now carry the full set. |
| 9 | The scroll bar rests thin, thickens, reserves no width | Partly verified. With the bar forced visible the rail thumb measured 216px for a 46 item list, so the track and its proportion are correct, and the shelf runs to the inset edge with no gutter held. The 4px to 10px hover growth was not driven. |
| 10 | Uninstalled badge and favorite state | Favorite verified: the one favorited game in the library renders as a filled gold mark. The badge is implemented but unexercised, see below. |
| 11 | Zero errors, zero new warnings, verified running | Verified. |

## Corrections made during implementation

- **Avalonia has no keyframe animator for `RenderTransform`.** Both the scan line and the click
  pulse crashed the app on startup with `No animator registered for the property RenderTransform`.
  Keyframes must target the individual transform properties, so the scan line animates
  `TranslateTransform.Y`, and the pulse became a transition on a class instead of an animation.
- **The pulse had to move to the end of `Controls.axaml`.** Avalonia resolves equal-weight style
  setters by application order, so a pulse defined in `Motion.axaml` lost to
  `Button.play:pointerover` defined later and never showed.
- **A hand-written `InitializeComponent` shadows the generated one.** The overlay crashed with a
  null reference on its own named fields until the manual method was removed.
- **`/template/` styling of the scroll thumb was replaced by an explicitly keyed `ControlTheme`,**
  referenced from the ScrollBar template by `Theme=`. Shape and state now live where they
  unambiguously apply.
- **The rail's hover was the base theme's, not Codec's.** A hovered row measured `#868584`, a
  bright grey that belongs to SimpleTheme's `ListBoxItem`. This turned out to be one instance of a
  general problem, so rather than clear it on one control the whole base palette was retuned to
  Codec's colors. The Settings toggles, which had been rendering blue, now measure `#E76C2B`.
- **The interaction washes were too faint to exist.** At `#09F3EDC9` a hovered rail row measured
  identically to an unhovered one. Hover is now `#14F3EDC9` and press `#1FF3EDC9`.

## A note on an earlier claim

The audit that preceded this spec corrected a number from earlier in this branch: WinUI did not
have 99 hover states against the port's 24. That count included theme brush names. WinUI declared
7 explicit `PointerOver` visual states and the port declares 33 `:pointerover` selectors.

## Known gaps

- **Hover and press could not be held.** Posted `WM_MOUSEMOVE` is followed by `WM_MOUSELEAVE`
  because the real cursor is elsewhere, so pointer-over is cancelled within a frame. A held rail
  hover measured `(24,22,20)` against `(22,20,18)` at rest, which shows the rule firing but not
  settling. Clicks, which are a single event rather than a held state, drive fine and were used
  throughout. The real cursor was deliberately not moved.
- **The card press and the Play pulse are therefore unproven.** Both hang off `Interaction`, whose
  press half needs a held pointer and whose pulse half fires on `Click` but completes in 210ms,
  inside the window where the synthetic pointer has already left.
- **The `NOT INSTALLED` badge is unexercised.** The library holds 46 games and `library.json`
  reports zero with `IsInstalled` false, so the binding has nothing to show.
- **Onboarding and the empty state are unexercised,** for the same reason as the previous round:
  the library is populated and onboarding is complete.
