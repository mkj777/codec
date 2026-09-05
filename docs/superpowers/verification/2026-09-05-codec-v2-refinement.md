# Codec v2 Refinement Verification

Date: 2026-09-05
Branch: `feat/avalonia-ui-migration`
Spec: `docs/superpowers/specs/2026-09-05-codec-v2-refinement.md`

## How this was checked

Clean non-incremental x64 Debug build, then the running window captured with `PrintWindow` at
1280 and maximised.

**Capture correction.** The earlier session's screenshots were silently wrong. The bitmap was sized
in window-rect units while the window renders at 120 DPI, so every capture lost the right and bottom
20 percent. That artifact was mistaken for a layout overflow and cost several wrong diagnoses.
Captures now size the bitmap by `GetDpiForWindow`, and the shelf turned out to have been laying out
correctly the whole time.

## Acceptance criteria

| # | Criterion | Result |
| --- | --- | --- |
| 1 | Cards 2:3 at every width | Verified. `CoverGrid` logged 4 columns of 232 at 1280 and 7 of 234 maximised, each with height derived at 1.5x. |
| 2 | Landscape artwork letterboxed | Implemented in `CodecImage.LetterboxWideArt`, not yet seen firing. Every cover in the test library is a real 600x900 capsule, so no wide asset was on screen. |
| 3 | Scrollbars reserve no width | Verified. The shelf runs to the inset edge and no gutter is held; the rail thumb floats over the list. |
| 4 | Deliberate hover, press and focus per control | Implemented across 13 controls plus a `:focus-visible` ring. Only the resting state is visible in a capture. |
| 5 | Library metrics match the density table | Verified. Tabs sit just under the caption strip, insets are 32, grid gap 16. |
| 6 | One-line count, source row absent with one source | Count verified as `46 games, 1.3 TB`. The absent-row case was not exercised: the test library has five sources. |
| 7 | Zero errors, zero new warnings | Verified. |
| 8 | Verified at runtime, not by inspection | Verified at both sizes. |

## Corrections made during implementation

- **`CoverGrid` never attached.** Its `TargetWidth` default was 210 and the XAML also set 210, so the
  change handler never fired. The default is now zero, which makes setting the property the thing
  that attaches the behaviour.
- **Width is left to `ItemsStretch="Fill"`.** Setting both dimensions risked a feedback loop between
  measured bounds and computed size. Fill fits the row by construction, so only the height is
  derived, using the same column count the layout picks.
- **The `ScrollViewer` template was reverted.** A hand-written one dropped the sizing behaviour that
  honours `HorizontalScrollBarVisibility`. SimpleTheme's own template is kept, and only the
  `ScrollBar` is re-templated, with `AllowAutoHide` lifting it off the layout.
- **`ScrollBar` binds `Maximum` and `Value`,** not `ScrollBarMaximum` and `ThumbValue`. Those belong
  to `ScrollViewer` and `Track` respectively.
- **The tab underline also fades.** A pure `scaleX` transform left all three underlines painted, so
  opacity carries the state and the scale carries the motion.

## Known gaps

- Wide-artwork letterboxing, the empty-library state, onboarding, and the single-source header are
  all implemented but unexercised, because the test library has 46 games with real capsules across
  five sources.
- Hover, press and focus states cannot be confirmed from a static capture. They are declarative
  style rules, but no pointer or keyboard state was driven through them.
