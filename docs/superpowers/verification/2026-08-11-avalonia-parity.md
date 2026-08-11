# Avalonia Migration Parity Record

Verification date: 2026-08-11  
Branch: `feat/avalonia-ui-migration`

The local Codec data directory contained `settings.json` but no `library.json`. Paths that require real games, Steam authentication, or an installed Velopack release are recorded as not exercised rather than inferred as passing.

| Surface or behavior | Status | Evidence or observation |
|---|---|---|
| Startup and service composition | Pass | Isolated executable opened and remained responsive. |
| Onboarding frame and navigation | Build verified | Three-stage frame and existing completion paths compile; real settings already mark onboarding complete. |
| Library loading | Not exercised | No persisted `library.json` was available. |
| Empty library | Pass | Captured at 1294 x 767; mascot, copy, Add, and Scan rendered correctly. |
| Sidebar search and grid search | Not exercised | Requires persisted games. |
| Sorting | Not exercised | Requires persisted games. |
| Source filter | Not exercised | Requires imported sources. |
| Install filter | Not exercised | Requires installed and owned-only games. |
| Favorite without navigation | Not exercised | Requires persisted games. |
| Add executable | Build verified | Avalonia storage picker and import command are wired; picker not opened during audit. |
| Scan and cancellation | Build verified | Existing coordinator commands and compact progress overlay are wired; scan was not started. |
| Game Details composition | Build verified | Hero, metrics, metadata, taxonomy, rail, and settings compile; no selected game was available. |
| Play and install request | Not exercised | Requires a launchable or owned-only game. |
| Executable and launch-script changes | Build verified | Commands use the Avalonia file-picker boundary. |
| Remove game | Build verified | In-app confirmation is wired; no game was available to remove. |
| Media image and video | Build verified | `CodecImage` and deterministic LibVLC view compile and native dependencies load on startup. |
| Franchise timeline and filters | Build verified | Overlay and filter bindings compile; no franchise data was available. |
| Settings | Pass | Opened through Windows UI Automation and visually captured. |
| Steam connect, QR, sync, disconnect | Build verified | PNG binding, commands, progress, and confirmation compile; no account flow was started. |
| Achievement maintenance | Not exercised | Requires a connected Steam account. |
| Notifications | Build verified | Shared bottom-right stack compiles; development update state is wired. |
| Reset confirmation | Build verified | In-app confirmation compiles; destructive reset was not invoked. |
| Tray hide and restore | Build verified | Avalonia `TrayIcon` service compiles; persistence choice was not changed during audit. |
| Close choice | Build verified | First-choice overlay is wired; current persisted choice is already `false`. |
| Direct close | Pass | Isolated process exited after a normal close request. |
| Update check | Pass in development mode | Startup check reached the expected development-mode failure path without crashing. |
| Update restart | Not applicable | No installed Velopack update was available. |

## Visual findings

- The initial Library pass exposed the toolbar in the empty state; it was corrected so the empty layout remains quiet.
- The Settings pass confirmed the intended single-panel hierarchy, restrained dividers, palette, and compact control order.
- The broad Impeccable scan only reported three vendored LibVLC HTML files under generated verification output. A source-only scan of `Views`, `Styles`, and `Media` returned no findings.
- Covers remain caption-free. No Continue hero was introduced.

## Remaining runtime evidence needed

Before release, repeat the behavior rows marked Not exercised on a machine with a populated library, a disposable Steam sign-in, and an installed Velopack build. These are release-verification gaps, not compile failures.
