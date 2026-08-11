# Codec Avalonia UI Migration Design

**Date:** 2026-08-11  
**Status:** Approved for implementation planning

## Objective

Replace Codec's WinUI 3 interface with an Avalonia UI application that preserves the current Windows-only functionality while establishing a quieter, more deliberate visual language based on the supplied Codec v2 references.

The migration is a one-way replacement. The existing WinUI project remains in the repository only as a visual and behavioral reference while the Avalonia application is built. It does not need to remain buildable or receive compatibility work during the migration.

## Binding Constraints

- Codec remains Windows-only for this migration.
- Existing Windows integrations remain valid, including Registry access, DPAPI, Windows launcher discovery, native process launching, tray behavior, file picking, and Windows-specific storage paths.
- Existing user-facing functionality must remain available after the Avalonia cutover.
- Models and services must end the migration free of WinUI dependencies and obsolete migration scaffolding.
- The existing WinUI project is removed only after Avalonia reaches functional parity.
- Installation and updates continue through Velopack using the same product and release flow.
- The project remains English-only and uses invariant English number and date semantics where applicable.
- No new automated tests are added or run as part of this migration. Validation uses isolated builds and focused runtime checks.

## Non-Goals

- Cross-platform runtime support.
- Rewriting working scanners, metadata providers, Steam integration, storage formats, or import behavior.
- Redesigning the Library information architecture.
- Redesigning the Game Details information architecture.
- Adding a Continue hero, cover captions, or new library browsing concepts.
- Upgrading Velopack as part of the UI migration.
- Keeping the WinUI application operational during the transition.

## Target Solution Architecture

### `Codec.Core`

A Windows-targeted class library containing the reusable application foundation:

- Models and persisted contracts.
- Storage and settings services.
- Scanning and launcher integrations.
- Steam authentication and library services.
- Metadata, artwork, update, and import services.
- Framework-neutral helpers.

The project targets `net9.0-windows10.0.19041.0`. Windows-specific implementation is allowed; UI-framework-specific implementation is not. The final project must not reference Microsoft.WindowsAppSDK, Microsoft.UI.Xaml, WinRT UI types, Avalonia controls, or UI-specific converters.

Existing namespaces remain under `Codec.*` unless a move materially improves ownership. This keeps the migration mechanical and avoids unrelated churn.

### `Codec.Avalonia`

The new executable desktop application containing:

- Avalonia application and desktop lifetime bootstrap.
- Window, tray, picker, clipboard, and other presentation adapters.
- Avalonia-specific view models and UI orchestration.
- Views, styles, control themes, converters, and image loading.
- The final Velopack startup integration.

The project targets `net9.0-windows10.0.19041.0` and x64. It uses Avalonia 12.1 and `Avalonia.Themes.Simple` as the neutral control-template foundation. Codec owns the visible control styling; FluentTheme is not used.

### Legacy WinUI Project

The existing root `Codec.csproj`, WinUI XAML, code-behind, and UI helpers remain available as a read-only implementation reference during migration. They are not adapted to consume `Codec.Core` and do not define compatibility requirements for new Avalonia abstractions.

At final cutover, the legacy project and WinUI-only files are removed. `Codec.Avalonia` becomes the product executable and the solution is simplified around the final Core and Avalonia projects.

## Application State and Data Flow

`ServiceHost` remains the composition root for the service graph and moves into `Codec.Core`. The Avalonia application creates one host and passes it into its root view model.

The flow is:

1. Velopack startup hooks run at the beginning of `Main`.
2. Avalonia initializes the desktop application lifetime.
3. `ServiceHost` creates storage, scanning, Steam, metadata, import, and update services.
4. The Avalonia root view model owns navigation and presentation state.
5. Views bind to view-model state and commands.
6. UI-thread dispatch, file dialogs, tray behavior, and window lifecycle stay in the Avalonia project.

View models expose data rather than framework objects. For example, the Steam QR code is represented as encoded PNG data or a framework-neutral stream contract, not `ImageSource`. Avalonia converts that data at the view boundary.

## Visual Direction

The supplied Codec v2 references define the visual system for the shell, sidebar, onboarding, controls, and overall rhythm. They do not replace the current Library or Game Details layouts.

### Color

- Background: `#1C1A17`
- Primary: `#E76C2B`
- Accent: `#D4A832`
- Secondary: `#C4956A`
- Text: `#F3EDC9`
- Muted text: `#9E8E78`

Near-black and warm brown surfaces provide depth without visible Fluent materials. Orange is reserved for primary actions and active state. Gold is reserved for favorites and restrained metadata accents.

### Typography

The bundled Nunito family remains the application typeface:

- Regular for descriptions and supporting text.
- Medium or SemiBold for navigation and compact labels.
- Bold only for page titles and primary messages.

The hierarchy must come from size, weight, spacing, and contrast rather than putting most text in SemiBold.

### Shape and Spacing

- Standard corner radii remain between 8 and 12 device-independent pixels.
- Large hero and overlay surfaces may use up to 16 pixels.
- Borders are used only for actionable controls or necessary separation.
- Major regions rely on spacing and tonal surface changes rather than nested bordered cards.
- Window and section padding follows the more generous rhythm shown in the references.
- Hover and press transitions are short and subtle, without WinUI ripple, reveal, or Fluent glow effects.

## Surface Designs

### Shell and Sidebar

The sidebar follows the new reference styling while retaining current capabilities:

- Codec branding and collapse affordance at the top.
- Search directly beneath the brand.
- Scrollable game list using muted text and a restrained selected state.
- Add and Scan actions remain fixed near the bottom.
- The sleeping mascot remains above Settings as the quiet brand anchor.
- Settings remains the final persistent action.

The main content should feel visually separate through background tone and spacing rather than a prominent enclosing border.

### Library

The current Library layout and behavior are preserved:

- Existing sorting controls.
- Existing source and installation filters.
- Existing game count and storage total.
- Cover-only responsive grid.
- Favorite control overlaid on each cover.
- Current loading, empty, and filtering behavior.

Game titles are not displayed beneath covers. No Continue hero is introduced. Covers are not wrapped in visible card chrome. Styling changes are limited to typography, spacing, radii, interaction states, and alignment needed to fit the new visual system.

### Game Details

The current Game Details composition is preserved:

- Hero artwork and logo handling.
- Back navigation.
- Play and game-settings actions.
- Review, playtime, release, and controller metadata.
- Developer, publisher, age rating, and price.
- Description, Genres, Themes, and Game modes.
- Media, external links, franchise content, and overlays.
- Remove-game and launch-path management.

The migration retains original artwork aspect-ratio behavior and does not add a title-text fallback when a logo is absent. Changes are limited to Avalonia implementation needs and restrained visual alignment with the new shell.

### Onboarding

Onboarding adopts the supplied Codec v2 direction more directly:

- Full-window presentation without the Library shell.
- Large, concise message and one obvious primary action.
- Quiet secondary Skip action.
- Mascot used as a balanced visual anchor.
- Minimal progress indicator.
- Final state uses `Your shelf is ready.` and `Open library`.

Onboarding continues to drive the existing scan/import setup rather than introducing a separate workflow.

### Settings, Steam, and Overlays

Existing capabilities are preserved in Avalonia-native in-app overlays. Stock operating-system message dialogs are not used for application flows that currently belong inside Codec.

Overlays share:

- One dimmed backdrop.
- One clear surface hierarchy.
- Short copy.
- Primary action first, safe cancellation always available.
- No stacked card decoration without an information-architecture purpose.

Steam QR sign-in, connection status, scan progress, update status, reset confirmation, close behavior, media viewing, franchise browsing, and game removal remain supported.

## Feedback and Error Handling

User feedback follows Codec's minimal communication rule:

- Show what is happening.
- Show progress when it is measurable.
- Show only the next useful action when something fails.

Provider-level request counts, retries, queue internals, and diagnostic detail remain out of the primary UI. Existing diagnostic logging stays available for development.

UI errors must not destroy the current library or replace visible content with an empty state. Background state is reconciled incrementally so covers and details do not flash or reset unnecessarily.

## Migration Blocks

### Block 1: Avalonia Foundation

Create `Codec.Core` and `Codec.Avalonia`, establish the desktop lifetime, SimpleTheme, Codec resources, fonts, assets, and an empty application shell. Confirm Velopack can remain the first startup operation without changing its package version.

### Block 2: Core and Presentation State

Move models and services into `Codec.Core`, remove UI-framework dependencies from that project, create Avalonia presentation state and converters, and restore startup/library loading through the existing service graph.

### Block 3: Shell and Library

Implement the sidebar, search, filtering, sorting, source controls, responsive cover grid, favorite interaction, navigation, loading state, and empty state. Preserve current Library information architecture and cover-only presentation.

### Block 4: Game Details

Implement the current Game Details surface and all related commands, media behavior, metadata rows, launch feedback, settings, removal, and back navigation.

### Block 5: Onboarding and Overlays

Implement the new onboarding presentation and migrate Settings, Steam sign-in, scan/import progress, notifications, confirmations, media, franchise, tray, and close behavior.

### Block 6: Parity and Cutover

Audit every current user-visible action against the legacy application, fix missing behavior, make Avalonia the packaged executable, verify Velopack startup/update integration, remove WinUI dependencies and legacy files, and simplify the solution.

## Validation

Each migration block must finish with:

- An isolated x64 build of the affected project with zero errors.
- A focused runtime check of the migrated surface.
- A source scan confirming that `Codec.Core` contains no WinUI or Avalonia UI types.
- A diff review to ensure the block does not broaden behavior beyond the migration.

The final cutover additionally requires:

- Avalonia application startup from the published output.
- Library load from existing persisted data.
- Scan, add, search, filter, favorite, details navigation, launch, settings, Steam, and update-flow checks.
- Velopack hook startup and packaging-path verification.
- Confirmation that no Microsoft.WindowsAppSDK or Microsoft.UI dependencies remain in the final product projects.

## Completion Criteria

The migration is complete when:

- Avalonia is the only shipping UI.
- Existing Windows functionality is available through the new interface.
- Library and Game Details preserve their approved layouts.
- Onboarding and the shell match the approved Codec v2 visual direction.
- Models and services are free of UI-framework dependencies and migration-only duplication.
- The legacy WinUI project and its package dependencies are removed.
- The isolated x64 build completes with zero errors.
- Velopack continues to install, start, and update the Avalonia executable through the established release flow.
