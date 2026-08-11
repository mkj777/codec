# Product

<!-- impeccable:product-schema 1 -->

## Platform

Windows desktop

## Users

PC players who own games across Steam, launcher ecosystems, third-party downloads, and manually added executables. They use Codec to find, understand, and launch their games from one quiet library.

## Product Purpose

Codec gathers a fragmented PC game collection into one fast, searchable library. Success means the player can find a game, see the information they need, and launch it without managing the surrounding launcher ecosystem.

## Positioning

Codec combines broad local discovery and launcher integration with deep game metadata while deliberately showing only the state and actions the player needs.

## Operating Context

Codec runs as a Windows desktop application, scans local drives and launcher libraries, stores a persistent local library, connects to Steam, enriches games from metadata providers, and launches or installs games through their existing platform paths.

## Capabilities and Constraints

- Windows-only during the Avalonia migration.
- Supports local executable import, heuristic scanning, Steam, Epic Games, Riot Games, and existing launcher-specific discovery.
- Preserves Registry access, DPAPI storage, Windows paths, native process launching, tray behavior, and Velopack installation and updates.
- Library and Game Details retain their current information architecture during the UI migration.
- Models and services must remain independent of the shipping UI framework.
- User-facing copy is English.

## Brand Commitments

- Product name: Codec.
- Voice: short, warm, plain, and deliberate.
- Mascot: the bundled sleeping red panda is a quiet brand anchor.
- Palette: background `#1C1A17`, primary `#E76C2B`, accent `#D4A832`, secondary `#C4956A`, text `#F3EDC9`, muted text `#9E8E78`.
- Typeface: bundled Nunito family.
- The approved Codec v2 references define the new shell and onboarding direction.

## Evidence on Hand

- Existing working WinUI interface and behavior remain in the repository as the migration reference.
- Existing persisted library data and real game artwork provide runtime content.
- Bundled icon, mascot, cover placeholder, and Nunito fonts live under `Assets/` during migration.
- No testimonials, customer claims, benchmarks, or commercial proof should be invented.

## Product Principles

- Less is more: show the activity and its progress, not internal stages.
- Preserve visible content and reconcile state without flashing or broad resets.
- Make action hierarchy obvious and keep non-interactive information visually quiet.
- Favor responsive performance and direct navigation over decorative complexity.
- Keep the application useful alongside the launchers and ecosystems players already use.
