# Codec Motion Restoration

Date: 2026-09-06
Branch: `feat/avalonia-ui-migration`
Predecessor: `docs/superpowers/specs/2026-09-05-codec-v2-refinement.md`

## Why

The WinUI to Avalonia port kept the ViewModel layer whole and rebuilt the view layer from
scratch. Every public ViewModel member survived, verified by diffing all six `MainViewModel`
partials against `master`: zero lost. What did not survive is the code-behind, which fell from
1084 to 176 lines in `MainWindow` and from 670 to 62 in `GameDetailView`. That is where all
79 storyboards and 55 double animations lived.

The result reads as instant and mechanical. This spec restores the motion, fixes the animation
rules that silently never run, and returns two pieces of dropped content.

An earlier note in this branch claimed WinUI had 99 hover states against the port's 24. That
count was wrong: it counted theme brush names. WinUI declared 7 explicit `PointerOver` visual
states; the port declares 33 `:pointerover` selectors. Hover coverage is not the deficit.
Motion is.

## The root defect

`Motion.axaml` declares three animations, `rise`, `fade` and `breathe`. All three are attached
through a static `Classes` value on controls that live permanently in the visual tree and are
shown with `IsVisible`.

Avalonia starts a style animation when its selector *becomes* active. A static class activates
once, at attach. `IsVisible="False"` does not detach a control, so it does not re-activate
anything. Every one of those entrances therefore plays invisibly during window load and never
again.

This is why the app has 56 `IsVisible` bindings and not one visible transition between states.
Fixing individual animations without fixing this would leave them equally dead, so the reveal
primitive comes first and everything else is built on it.

## Deliverables

### 1. `Reveal`, a show and hide primitive

New `Codec.Avalonia/Controls/Reveal.cs`. Attached properties on `Control`:

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Reveal.IsOpen` | `bool` | `false` | the state the caller binds |
| `Reveal.ExitDuration` | `TimeSpan` | 220ms | how long to keep the control alive while it leaves |

Opening sets `IsVisible` true, then adds the `revealed` class on the next dispatcher pass so
the control has a chance to be laid out at its resting values first. Closing removes `revealed`
immediately, waits `ExitDuration`, and only then sets `IsVisible` false. Reopening during that
wait cancels it.

The class carries the motion, so it re-fires on every open. That is the whole point.

Companion styles in `Motion.axaml`:

- `.reveal` starts at opacity 0 and `translateY(10px)`, transitions both.
- `.reveal.revealed` lands at opacity 1 and no transform.
- `.reveal-soft` is the same without the translate, for full page swaps where a moving page
  would read as a slide rather than a fade.

Applied to: the library and details pages (crossfade), the Settings overlay, the close prompt,
onboarding, the notice pill, the library empty state, the initial loading panel, and the game
details disclosure body.

### 2. The startup sequence

New `Codec.Avalonia/Views/Overlays/StartupOverlay.axaml` and code-behind, restoring the timing
from `master`'s `MainWindow.xaml.cs:313-460`:

| ms | what |
| --- | --- |
| 120, +70 per tile | nine cover tiles fade in and rise 18px over 360ms |
| 450 | a 2px orange line begins sweeping the mosaic top to bottom, 870ms |
| 620 to 1320 | that line rises to 0.9 opacity and fades back out |
| 1350 | the 390px mosaic contracts to 0.84 over 520ms |
| 1450 | the mosaic settles back to 0.28 opacity over 430ms |
| 1480 | the icon and wordmark fade up through it over 420ms |
| ready | the overlay fades out while scaling to 1.035, 300ms |

The per-tile stagger is expressed as `Delay` on each tile's own transitions rather than as nine
animations, so adding a single `go` class to the mosaic starts the whole sequence. This is
possible because `TransitionBase.Delay` exists, verified in the 12.1.0 XML documentation.

The tiles bind `StartupCoverGames`, which `MainViewModel.PrepareStartupCoverGames` has been
populating on every launch since the migration with no consumer. Each tile keeps a flat brand
colour behind its image, so a first run with no cached art still animates.

Gating matches the original: the overlay stays up until both a 2.2s floor and the library load
have completed, and background maintenance starts only after it leaves. The load is awaited
inside a `try`/`finally` so a failure cannot strand the user behind an opaque overlay.

Reduced motion is honoured through `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)`, the same
signal `UISettings().AnimationsEnabled` reads. When animation is off the overlay shows its
resting composition and leaves without motion.

### 3. Interaction behaviours

New `Codec.Avalonia/Controls/Interaction.cs`:

- `Interaction.PressClass` toggles a `pressed-in` class on any control from tunnelled pointer
  events. Needed because Avalonia has no ancestor selector, and the cover card's press state
  belongs to the card while the pointer events belong to the hitbox inside it.
- `Interaction.PulseOnClick` adds a `pulse` class on `Click` and removes it when the animation
  is over.

This restores the hero button choreography, which in WinUI was six wired pointer handlers per
button driving hover scale 1.025, press scale 0.97 with a 1.5px sink, and a click pulse that
overshot to 1.043 at 85ms before settling at 190ms. The overshoot is most of what "responsive"
felt like and none of it survives today.

### 4. Fixes to existing rules

- **The card press does nothing.** `Library.axaml` sets `RenderTransform` on
  `Grid.cover-card Button.cover-hitbox:pressed`. That is the transparent full-bleed hitbox, so
  translating it moves no pixels. Replaced by `Grid.cover-card.pressed-in`.
- **Two `Transitions` blocks silently drop properties.** `Button.play` and `Button.favorite`
  re-declare `Transitions`, which replaces the base `Button` set rather than extending it. Play's
  `BorderBrush` and `Foreground` and favorite's `Foreground` snap today.
- **The sidebar snaps.** `MainWindow.axaml` binds `Width` through a converter with no transition,
  so 240 to 0 happens in one frame. WinUI faded and slid it 18px over 150ms.

### 5. The scrollbar

`master` styled scrollbars in zero lines, so what the app used to have was WinUI's stock overlay:
a thin resting indicator that thickens as the pointer approaches. The port rests at 8px and jumps
to 10px with no transition on `Width`.

The bar keeps a constant 10px so it stays easy to hit, and the visible thumb is inset by the
Thumb's own `Padding`: 3px each side at rest, giving 4px of visible bar, and 1px on hover, giving
8px. `ThicknessTransition` animates between them. The hit target and the drawn weight are
separated, which is what made the WinUI one feel slim without being fiddly.

### 6. Two pieces of dropped content

- The `NOT INSTALLED` badge on library covers. `Game.IsNotInstalled` still exists in
  `Codec.Core`; no view has bound it since the migration.
- The favorite mark never reads as set. Views bind only `FavoriteOpacity`, so a favorited game
  differs from an unfavorited one by 0.38 opacity on a small icon. `Game.IsFavorite` drives a
  filled gold state instead.

## Out of scope

- The wide-artwork blur backdrop behind letterboxed covers. The flat surface is a decision from
  the previous round, not an oversight.
- The multi-toast notification stack. One notice at a time is a Codec v2 decision.
- The two-line library header counts. Replaced deliberately by `LibrarySummaryText`.

## Acceptance criteria

1. The startup sequence plays on launch, covers the library load, and the app is usable after it.
2. A failed library load still dismisses the overlay.
3. Opening and closing Settings twice animates twice.
4. The library and details pages crossfade rather than cut.
5. The sidebar collapses and expands over time, not in one frame.
6. Pressing a cover moves the card.
7. Clicking Play pulses.
8. No interaction rule changes a property its control does not transition.
9. The scrollbar rests thin, thickens under the pointer, and reserves no layout width.
10. An uninstalled game shows its badge; a favorited game reads as favorited.
11. Zero errors, zero new warnings, verified running rather than by inspection.
