# Verification: WinUI parity restoration

Date: 2026-09-12
Plan: `docs/superpowers/plans/2026-09-12-winui-parity-restoration.md`
Branch: `feat/avalonia-ui-migration`

## How this was checked

Debug build, the real library of 47 games, 1280x760 at 96 DPI. A small console harness
captured the client area with `PrintWindow` (`PW_RENDERFULLCONTENT`), drove the app with posted
pointer and key messages, resized it through `SetWindowPos`, read pixels back out of the
captures, and listened to the app's debug output the way DebugView does. Ctrl+F was the one
input that had to go through `SendInput`: Avalonia reads the modifier off the real keyboard
state, so a posted Control never registers.

The library carries no achievement counts and its trailers are DASH streams, so Silent Hill 2
was seeded with 17 of 41 achievements and one local clip for the duration of the run; the
library, settings and scan cache were restored byte for byte afterwards.

## Results

| Check | Result |
| --- | --- |
| Drawer slides in from the right | Verified. Its edge measured at x=965, 955, 950 and its surface at 34, 38, 40, 41 (of 41) across the first 200 ms of separate opens |
| Click outside closes the drawer, the gear toggles cleanly | Verified. Hero click closed it; gear open, closed, open; a click inside left it open |
| Media overlay: Next three times, Left twice | Verified. Captures fingerprinted as 0, 1, 2, 3, 2, 1; the two Left frames matched the earlier Next frames pixel for pixel |
| Trailer: no autoplay, Play, Pause, seek | Verified after four fixes, below. Idle 2.5 s: Play glyph, 0:00. Play: Pause glyph, clock and slider moving. A track click at 50% landed on 1:22 of 2:44, a thumb drag to 25% on 0:41 |
| Scrim closes the media and franchise overlays | Verified, both |
| Franchise timeline | Verified. Line, a dot per entry, above and below alternation, platform logos under the year, Mainline/Extended/All chips |
| Three layouts at 980, 1100, 1400 | Verified. Insets 22, 34, 48; meta column dropped under the copy, 220, 260. "Bloober Team SA" rendered 99, 101, 107 px wide, the 13, 13.3, 14 px the formula gives |
| The seven facts on the right game only | Verified on Silent Hill 2 (original release, review count, discount, controller, achievement hairline, "+14" tile) and absent on 2XKO |
| Favourite from the shelf | Verified. Gnorp gained the rail star; God of War lost its own when unfavourited |
| Ctrl+F opens the rail; nothing under Settings | Verified. Rail opened with the rule orange at y=81; under Settings the rule stayed dim |
| Escape dismisses the close prompt | Verified. Prompt gone, window still there |
| Right-click on details, twice | Verified. Drawer closed, then Back to the shelf |
| Gating during Refresh covers | Verified. Add dimmed from 231,94,28 to 148,65,24 and the rail text from 158 to 90; Settings' maintenance and debug buttons greyed; Add opened nothing; pill read "Update Cover, 0%" |
| Add-by-executable errors | Two-line pill rendered through Check IDs ("Microsoft Windows Operating System: no Steam id"). A real non-game, notepad.exe, takes the queued path and shows the existing "That file is not a game" notice, which is the intended behaviour; the two-line pill is for the import throwing, as item 12 says |
| Search focus | Verified. Rule 231,108,43 with focus, dim without |
| Debug group in Settings | Verified. Four buttons. `IsDebugMode` was defined all along; the plan's item 15 was a misread |
| Steam "Connected" and "Syncing...", admin second line | Not exercised: no Steam account, no elevation. Bindings read as described |
| Test project | 179 of 179 pass after the fixes below |

## Defects found running it

1. Stepping past the first trailer crashed the process. `CodecVideoView` disposed its player
   on detach, and LibVLCSharp's `VideoView` cleared the player's window handle on a later
   dispatcher pass, on the released object. The surface is handed a null player before the
   dispose now.
2. The transport and the step buttons were invisible on trailers. The surface is a native
   child window and paints over everything Avalonia composites on top of it. The transport
   sits under the picture now and the steps stand beside it.
3. Play opened a window of VLC's own. `VideoView` only passes the window handle when a player
   is assigned after it is initialised and attached; the constructor did it too early. The
   player is bound on the surface's own `AttachedToVisualTree` and `Initialized`.
4. Even then, a second window opened two seconds in. The adaptive demuxer switched
   representation, which is a new decoder asking for a new video output while the old one
   still held the embedded window. `--adaptive-logic=highest` keeps it to one output.
5. Every screenshot in the overlay was also building a libvlc instance and a native window,
   because one template held both halves. A template selector builds only what the item is.
6. The tight layout's factline ran under the Back button. The lower third outgrows the band
   there and starts at its top; a 52 px top inset keeps it clear.
7. The age rating value clipped at the column edge ("ESRB 17+, PEGI 18, US"). It wraps.
8. The slider thumb was the theme's blue. The palette never re-declared `ThemeAccentBrush`;
   the video view overrides it for its own subtree.
9. A thumb drag on the slider would never have seeked: the thumb marks its own press and
   release as handled. The handlers listen past that now.

Also: the middle dot separators in the review count, the progress pill and Check IDs became
commas and colons, and the duration shows before Play by parsing the media ahead.

## The failing tests

Twenty at the start of the round, not the plan's twenty-one, and two causes rather than one:

- `IgdbServiceTests` (17): the service batches lookups through IGDB's multiquery endpoint and
  the fake handler still answered as if each endpoint were called directly. It unwraps a batch
  now. That left one: the exact-title retry queried `where name = "Sim City"`, because the
  name is camel-split before the search. The split helps the search and defeats the exact
  query; the retry carries the unsplit name now. The scan log of 2026-09-07 shows the real
  cost: `SimCity ... REJECTED no IGDB release-year match`.
- `AppSettingsTests` (2): asserted `ScanOnStartup` defaults to true. Commit 79aec29 of
  2026-07-13 turned it off deliberately so onboarding decides. The tests follow that.
- `Coordinator_ContinuesProcessingAfterFailure` failed once (expected 2, got 1) and passed in
  every later run. Timing-sensitive; not touched.
