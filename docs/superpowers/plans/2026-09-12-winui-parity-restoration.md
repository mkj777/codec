# Plan: restore what the WinUI build had and the port lost

Date: 2026-09-12
Branch: `feat/avalonia-ui-migration`
Source: `docs/superpowers/verification/2026-09-09-winui-parity-gaps.md`, section A only.
Section B items are v2 decisions and stay as they are.

## Where things stand

The working tree already holds an uncommitted implementation of section A, written on
2026-09-09 between 18:15 and 18:53 (22 modified files, 3 new ones, 1126 lines added). It was
not produced in this session. It builds with zero warnings and zero errors. Nothing in it has
been run.

Checked against the 17 items of section A:

| # | Item | In the working tree |
| --- | --- | --- |
| 1 | Drawer, media and franchise overlays fade | Done. `reveal-drawer` slides the drawer in from the right; the overlays use the Settings pattern (scrim `reveal-soft`, surface `reveal`) |
| 2 | Click outside the drawer closes it | Done. Tunnelled pointer handler on the page root, excluding the drawer and the gear that opened it |
| 3 | Media overlay: arrows, scrim click, video transport, no autoplay | Done except keyboard. Previous and Next buttons, scrim handler, play, pause, seek and elapsed time in `CodecVideoView`, autoplay off. Left and Right arrow keys do nothing yet |
| 4 | Franchise timeline | Done. Line, dot, above and below alternation, platform logos. Radio buttons became `chip` toggles |
| 5 | Three responsive layouts | Done. `comfortable`, `compact`, `tight` classes on the page root at 1080 and 820 px of page width, meta column 260, 220, then dropped below the copy; value type 13 to 15 px and platform icons 16 to 22 px scale across 740 to 1600 px |
| 6 | Seven unbound facts | Done. Own-on-Steam badge sits on the hero action row rather than in the drawer; review count, original release, discount, controller note, achievement hairline, "+N" tile |
| 7 | Favourite star in the rail | Done |
| 8 | Ctrl+F opens the rail first, ignored under overlays | Done |
| 9 | Escape dismisses the close prompt | Done |
| 10 | Right-click on details is Back | Done, one layer at a time, shared with Back and Escape |
| 11 | UI gating | Done. `IsUiEnabled` is back, `RunExclusiveAsync` wraps Refresh covers, Add and the debug commands; search, list, Add, Scan and Settings bind to it |
| 12 | Add-by-executable errors | Done, reported through the pill as a two-line action result |
| 13 | Search focus state | Done. The rule under the box goes orange on `:focus-within` |
| 14 | Steam "Connected" and "Syncing..." | Done |
| 15 | Debug menu | **Broken.** Settings binds `IsDebugMode` and `MainViewModel.Debug.cs` documents it, but nothing defines it. The binding fails silently and the group never shows |
| 16 | Test project compiles | Done. `LauncherUris` moved to Core. The project now builds and runs: 158 pass, 21 fail on stale assertions unrelated to the port (see step 4) |
| 17 | Admin warning second line | Done. `NoticeDetail` carries it |

So the plan is: finish the two open ends, verify all of it running, and commit in reviewable
pieces.

## Step 1: finish the open ends

**1a. Define `IsDebugMode`.** In `MainViewModel.Debug.cs`:

```csharp
public static bool IsDebugMode =>
#if DEBUG
    true;
#else
    System.Diagnostics.Debugger.IsAttached;
#endif
```

A static property binds fine with `CompileBindings=False`. The WinUI build's own definition
was not recoverable, so this follows the comment in the file: a debug build shows it, a release
build only under a debugger.

**1b. Arrow keys in the media overlay.** In `MainWindow.OnWindowKeyDown`, before the Back and
Escape branch:

```csharp
if (viewModel.IsMediaOverlayOpen && e.Key is Key.Left or Key.Right && e.Source is not TextBox)
{
    DetailsHost.StepMedia(e.Key == Key.Right ? 1 : -1);
    e.Handled = true;
    return;
}
```

with `StepMedia` on `GameDetailView` calling `MediaCarousel.Next()` or `Previous()`. The window
handler is where the other shortcuts live, so it stays there rather than growing a second key
handler on the page.

**1c. Read the diff once more as a reviewer.** Two things to look at specifically:

- `Grid.Row` and `Grid.Column` as style setters on `hero-meta` and `lower-links`
  (`GameDetails.axaml`). The markup must not set them locally or the tight layout never takes.
  It does not today; keep it that way.
- `RunExclusiveAsync` returns silently when `IsUiEnabled` is already false. A second click on
  Refresh covers therefore does nothing, which is the intent, but the button stays enabled. Bind
  the Settings maintenance and debug buttons to `IsUiEnabled` as well so they grey out.

## Step 2: verify running

Launch the debug build and check each item against a real library, with the capture harness
used in the earlier rounds (`PrintWindow` at DPI-correct size, posted pointer messages for
clicks, `WM_NCHITTEST` where a hit-test matters).

| Check | Expect |
| --- | --- |
| Open a game, open Game Settings | Drawer slides in from the right over 220 ms; a capture at +60 ms shows it partway |
| Click the hero artwork with the drawer open | Drawer closes; clicking the gear does not close and reopen it |
| Open a screenshot from the media strip, click Next three times, press Left twice | Index moves 1, 2, 3, then 2, 1; arrows and the visible item agree |
| Open a trailer | It does not start on its own; Play starts it, the glyph flips to Pause, the slider moves, seek lands within a second of the target |
| Click the scrim of the media overlay and of the franchise overlay | Both close |
| Open a franchise with more than two entries | Entries alternate above and below one line, a dot per entry, platform logos under the year |
| Resize the window to 980, 1100 and 1400 wide with the rail open | Page width 740, 860, 1160: tight, compact, comfortable. In tight the meta rows sit under the copy; value type reads 13, 13.3 and 14 px |
| A remaster, a discounted game, a game with achievements, a game with more than three media items | Original release line, discount, achievement hairline and the "+N" tile each appear on the right game and nowhere else |
| Favourite a game from the shelf | Star appears in the rail row |
| Collapse the rail, press Ctrl+F | Rail opens, caret lands in Search; with Settings open, Ctrl+F does nothing |
| Close the window with no tray choice saved, press Escape | Prompt closes, window stays |
| Right-click the details page with the drawer open, then again | Drawer closes, then the page goes Back |
| Start Refresh covers, then press Add | Add, Scan, Search, the list and Settings are disabled until the pass ends; the pill reports progress |
| Add an executable that is not a game | Pill reads "Could not add that game" with the reason beneath |
| Tab into Search | The rule under it turns orange |
| Debug build, open Settings | A Debug group with four buttons is at the bottom |

Anything that fails gets fixed in place before the commit that carries it.

## Step 3: commit in four pieces

Each on its own so the review and any revert stay small:

1. `fix: give the details page its motion, dismissal and keyboard back` (items 1, 2, 3, 8, 9, 10)
2. `feat: bind the facts and the timeline the port dropped` (items 4, 5, 6, 7)
3. `fix: gate the shelf during whole-library work and report add failures` (items 11, 12, 13, 14, 17)
4. `chore: bring the test project back and restore the debug corner` (items 15, 16)

Before each: the em-dash count over the changed files must be 0.

## Step 4: the 21 failing tests

Out of scope for parity but now visible, and a red suite hides the next regression. Two groups:

- `AppSettingsTests` (2): assert `ScanOnStartup` defaults to true; the model defaults to false.
  Decide which is right with the user; the setting was deliberately turned off in their own
  settings, so the model is probably right and the tests are stale.
- `IgdbServiceTests` (19): every lookup through the fake `HttpClient` returns null or empty.
  Something between the handler and the query changed in Core (a token step, a proxy, a
  resource limiter) so the stub never answers. One cause, one fix, but it needs a debugger
  session on a single test first.

Do this as its own commit after the four above, or not at all in this round.

## Files

Touched by the working tree already: `GameDetailView.axaml` and `.axaml.cs`,
`FranchiseTimelineControl.axaml`, `MainWindow.axaml` and `.axaml.cs`, `SettingsView.axaml`,
`NoticePill.axaml`, `CodecVideoView.axaml` and `.axaml.cs`, `Styles/GameDetails.axaml`,
`Styles/Motion.axaml`, `Styles/Controls.axaml`, `Converters/ValueConverters.cs`, the
`MainViewModel` partials, `Game.cs`, `Codec.Core/Helpers/LauncherUris.cs` (new),
`MainViewModel.Debug.cs` (new), three test files.

Step 1 adds to: `MainViewModel.Debug.cs`, `MainWindow.axaml.cs`, `GameDetailView.axaml.cs`,
`SettingsView.axaml`.
