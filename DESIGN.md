# Codec Design System

Codec is a quiet shelf for a real game collection. The interface should feel like a focused desktop product, not a Windows settings page or a storefront.

## Visual language

- Background: `#1C1A17`
- Primary action: `#E76C2B`
- Favorite and warning accent: `#D4A832`
- Secondary accent: `#C4956A`
- Primary text: `#F3EDC9`
- Muted text: `#9E8E78`
- Typeface: Nunito, with weight carrying hierarchy before size
- Corners: 8-14 px for controls and surfaces; round only genuinely circular actions
- Borders: one restrained warm-gray line only where separation is not already clear from tone or space

## Layout rules

- The sidebar owns navigation, search, Add, Scan, the mascot, and Settings in that order.
- Library covers are the content. Never render titles beneath them and never add a Continue hero.
- Game Details keeps the existing information order: hero actions, summary metrics, metadata and description, taxonomy, then media and links.
- Overlays use one dimmed backdrop and one clear surface. Avoid nested cards inside settings or confirmations.
- Use 8 px as the small spacing rhythm, 14-18 px between related groups, and 22-28 px for major insets.

## Interaction rules

- Orange is reserved for the primary next action.
- Hover and press feedback changes tone or opacity without large motion.
- Background work reports one activity and one progress value.
- Copy is short, warm, and in English.
- Destructive actions always require an in-app confirmation; light-dismissible surfaces never dismiss a destructive choice.

## Brand signature

The sleeping red panda is the single memorable illustration. It belongs in onboarding, the empty state, and the sidebar rest area; do not multiply it into decorative badges or backgrounds.
