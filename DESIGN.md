# Codec Design System

Codec is a quiet shelf for a real game collection. The interface should feel like a focused desktop product, not a Windows settings page or a storefront.

The approved Codec v2 reference defines the current direction. `docs/superpowers/specs/2026-08-13-codec-v2-design.md` holds the exact values, and `docs/superpowers/specs/2026-09-06-codec-motion-restoration.md` holds the motion contract.

## Visual language

- Background: `#1C1A17`
- Primary action: `#E76C2B`
- Favorite and warning accent: `#D4A832`
- Secondary accent: `#C4956A`
- Primary text: `#F3EDC9`
- Muted text: `#9E8E78`
- Typeface: Sansation carries display type - page titles, the wordmark, the Play pill. Nunito carries everything else. Weight carries hierarchy before size.
- Corners: 8-10 px for controls, list rows, and covers; 14-20 px for surfaces; fully round only for genuinely circular actions and the Play and notice pills.
- Separation: warm alpha lines over whatever sits beneath them, `#17F3EDC9` for structure and `#0EF3EDC9` for rows, used only where tone and space do not already separate.
- Interaction washes: `#14F3EDC9` on hover and `#1FF3EDC9` on press. Quiet is the goal, invisible is not: below roughly 5 percent alpha a hover measures the same as rest on a near-black surface and the app reads as broken rather than as restrained.
- The base theme's own palette is retuned to these colors rather than left at its defaults. Anything not templated explicitly still draws from it, so a stray grey hover or blue accent means a theme key was missed, not that a control needs its own style.

## Window rules

- The window is a Windows window first. Caption buttons use platform metrics, 46 wide and the
  full height of the strip, square, flush into the corner, with the standard red close. Codec's
  own button language does not apply here.
- Caption elements carry a `WindowDecorationProperties.ElementRole` so the OS knows what they
  are. The maximize role is what produces the Snap Layouts flyout; without it the buttons are
  ordinary client-area controls and Windows offers nothing.
- Anything in the top strip that is not a control drags the window, including the rail header.
  An element needs a background to be hit-testable at all.
- The window remembers where it was left: position, size and maximized state, validated against
  the screens that still exist.
- Codec stays opaque. Mica was measured and rejected: at a tint that preserves the palette it is
  invisible, and at one where it reads it turns the rail and caption neutral grey.

## Layout rules

- The sidebar owns navigation, search, Add, Scan, the mascot, and Settings in that order.
- Library covers are the content. Never render titles beneath them and never add a Continue hero.
- The library header filters in two stable rows: view tabs and the library count, then source chips and sort. Controls never move as sources appear.
- Game Details is the ambient cinema mood: sharp full-bleed artwork with the page reading as a lower third. It keeps the existing information order - hero actions, summary metrics, metadata and description, taxonomy, then media and links - continuing into a quiet second section below the first viewport.
- Overlays use one dimmed backdrop and one clear surface. Prefer hairline-separated rows over cards; avoid nested cards anywhere.
- Use 8 px as the small spacing rhythm, 14-22 px between related groups, and 44-68 px for major insets.

## Interaction rules

- Orange is reserved for the primary next action. In the sidebar it speaks through color rather than fill.
- Hover and press feedback changes tone or opacity without large motion. Covers are the one exception: they lift 7 px and take on a ring in their own artwork color.
- Motion is short and eased on `0.2, 0.7, 0.2, 1`. Nothing bounces and nothing spins.
- Nothing appears or disappears instantly. Anything that comes and goes fades and rises through `Reveal`, which drives a class rather than visibility so the entrance runs every time and the exit is seen. An animation attached to a fixed class on a permanently mounted control runs once, at startup, and never again; that is a bug, not a style.
- Primary actions answer a click as well as a hover. Play and Back overshoot to 1.04 and settle, which is what separates responsive from merely styled.
- Scroll bars overlay, reserve no width, and rest thin. The lane carries the weight, 4 px at rest and 10 px under the pointer; the thumb only fills it.
- Codec opens on its own collection. The launch sequence covers the library load rather than following it, and background work starts only once the shelf is on screen.
- Background work reports one activity and one progress value, never a raw count or an internal stage, and never blocks the library.
- Progress is a hairline, not a bar.
- Copy is short, warm, and in English.
- Destructive actions always require an in-app confirmation; light-dismissible surfaces never dismiss a destructive choice.

## Brand signature

The sleeping red panda is the single memorable illustration. It belongs in onboarding, the empty state, and the sidebar rest area; do not multiply it into decorative badges or backgrounds. It appears once at a time: the sidebar hides it whenever the library is empty, so it never competes with the empty-state illustration.
