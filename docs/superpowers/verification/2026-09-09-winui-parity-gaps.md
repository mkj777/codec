# What the Avalonia port still lacks against the WinUI build

Date: 2026-09-09
Branch: `feat/avalonia-ui-migration`
Compared: WinUI project at `42142df` (last commit before `refactor: extract Codec core from WinUI`)
against the working tree at `e0616f7`.

Method: every WinUI view, code-behind and ViewModel partial was read side by side with its Avalonia
counterpart. ViewModel members were diffed by name: the only WinUI member with no successor is
`IsLibraryVisible` (renamed), so the losses below are all in the view layer, in wiring, or in
behaviour the views used to own. Each item is marked as an accidental loss or as a documented v2
decision, because the two need different answers.

## A. Lost by accident, still missing

These appear in no spec as a decision. They simply did not survive the port.

| # | What the WinUI build did | State in Avalonia | Where |
| --- | --- | --- | --- |
| 1 | Game Settings drawer, media overlay and franchise overlay appeared through the same fade as everything else | All three toggle raw `IsVisible`, so they cut in and out. Breaks the "nothing appears instantly" rule in `DESIGN.md` | `GameDetailView.axaml` lines 381, 430, 455 |
| 2 | Clicking anywhere outside the Game Settings drawer closed it | No outside-click handler; only the X button, Escape and Back close it | `GameDetailView.axaml.cs` |
| 3 | Media overlay: previous and next arrows (FlipView), scrim click closed it, videos had transport controls and did not autoplay | `Carousel` has no navigation, so only the item that was clicked can be seen; scrim has no handler; `CodecVideoView` is a bare VLC surface with no play, pause or seek, and it autoplays | `GameDetailView.axaml` 428 to 451, `CodecVideoView.axaml` |
| 4 | Franchise overlay was a timeline: a continuous line, a dot per entry, entries alternating above and below the line, platform logos under each entry | A flat horizontal strip of cards. The ViewModel still computes `IsAbove`, `IsBelow` and `PlatformLogoUris`; the view ignores them. Filters survived | `FranchiseTimelineControl.axaml` |
| 5 | Details page had three responsive layouts (comfortable, compact, tight) and scaled value type and platform icons with the pane width | One fixed layout with 48px insets, a 240px meta column and a 520px copy column | `GameDetailView.axaml` |
| 6 | Details showed: "You also own this on Steam" badge, Steam review count, original release date, price discount, controller "Recommended" note, achievement progress bar, and a "+N" button for media beyond the first three | None are bound. All seven values are still computed in `Game.cs` (`IsAlsoOwnedOnSteam`, `DisplaySteamReviewCount`, `OriginalReleaseDisplay`, `PriceDiscount`, `HasControllerRecommendation`, `SteamAchievementsTotal`, `AdditionalMediaDisplay`) | `GameDetailView.axaml` |
| 7 | A star beside every favourited game in the sidebar list | Rail rows show the name only | `MainWindow.axaml` rail item template |
| 8 | Ctrl+F and Ctrl+S opened the rail first if it was collapsed, and were ignored while any overlay owned the screen | Focus goes into the 0px wide search box inside the collapsed rail, from any state | `MainWindow.axaml.cs` `OnWindowKeyDown` |
| 9 | Escape dismissed the first-close prompt | The key handler never looks at `IsCloseChoiceVisible` | `MainWindow.axaml.cs` |
| 10 | Right-click on the details page was Back | Gone. Right-click on the rail now toggles the rail instead, which is new | `MainWindow.axaml.cs` |
| 11 | `IsUiEnabled` disabled search, the game list, Add, Scan and Settings while a refresh, an add or a reset ran | `IsUiEnabled` no longer exists. Only Scan is gated (`CanStartScan`). Refresh covers has no reentry guard and can be started twice from Settings | `MainViewModel.Library.cs` `RefreshCoversAsync` |
| 12 | Add by executable showed the error message in a dialog when the import threw | `AddGameFromPickerAsync` has no try/catch; an exception goes to the crash log and nothing is shown | `MainViewModel.cs` line 639 |
| 13 | The sidebar search box showed focus with an orange border | `rail-search:focus` sets nothing visible. There is no focus indication at all | `Controls.axaml` line 495 |
| 14 | Settings showed "Connected" under the Steam account and the sync button read "Syncing..." while busy | `SteamSyncButtonText` is still computed but the button says "Sync now" always; no "Connected" caption | `SettingsView.axaml` |
| 15 | Debug menu in Settings (Debug Data dump, Check IDs, Start Steam Silent) behind `IsDebugMode` | Gone with `IsDebugMode`. Only Refresh covers survived, and it is now always visible | `SettingsView.axaml` |
| 16 | The test project built and ran | `Codec.Tests` references only `Codec.Core` but two files `using Codec.ViewModels` (`MainViewModel.BuildEpicLaunchUri`, `BuildSteamInstallUri`). The project has not compiled since the split | `Codec.Tests.csproj` |
| 17 | Admin warning had a second line: "Some features may not work correctly." | The pill shows the title only | `MainViewModel.Presentation.cs` |

## B. Changed on purpose by the v2 design, worth a second look

Documented in `docs/superpowers/specs/2026-08-13-codec-v2-design.md` or the motion spec. Listed so the
decision can be reversed deliberately rather than rediscovered.

| What changed | Where it was decided | Note |
| --- | --- | --- |
| Full-screen scan and loading overlay (running mascot, title, subtitle, bar) replaced by the notice pill | v2 spec section 6 | `LoadingTitle` and `LoadingSubtitle` are still set during scans but only the cold-start panel binds them |
| Bottom-right notification stack (several toasts at once) replaced by one pill | v2 spec section 6, motion spec out of scope | Nothing is lost in content; simultaneous events queue |
| Two-line library count (installed, not installed, storage) replaced by one line | motion spec out of scope | `InstalledLibraryGameCount` and `NotInstalledLibraryGameCount` still exist |
| "Not installed" as its own filter removed; tabs are All, Installed, Recent | v2 spec section 4 | There is no longer a way to see only games that are owned but not installed |
| Blurred backdrop behind letterboxed wide covers replaced by a flat surface | motion spec out of scope | |
| Blurred, darkened backdrop plus sharp foreground on the details hero replaced by a band sized to the artwork with gradient veils | this session | |
| Onboarding rebuilt as three steps. The standing mascot lost the two tilted colour cards behind it, and the narrow-window layout (below 780px) | v2 spec section 7 | The cards and the adaptive layout are not mentioned in the spec, so they were dropped in passing |
| Reset confirmation became an inline row. The "this will delete / this will not delete" lists became one sentence | v2 spec section 7 | The reassurance that installed games and saves are safe is gone |
| Mica backdrop | window chrome verification | measured and rejected |

## C. Already restored in earlier rounds

Startup mosaic sequence, sidebar collapse motion, reset overlay, cover hover and press, Play click
pulse, overlay fades through `Reveal`, sidebar orange hover, sidebar ellipsis, details logo, rail
selection on left click, wordmark as Back, window placement, native caption buttons, thin
scrollbar, uninstalled badge, favourite state on covers, first-run flow.
