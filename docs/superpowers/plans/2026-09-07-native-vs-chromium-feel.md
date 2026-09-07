# Why Codec does not feel like Claude, Steam or Spotify yet

Date: 2026-09-07
Branch: `feat/avalonia-ui-migration`
Question: would Tauri or Electron look better, faster and more professional than Avalonia?

## Verdict

No. Switching would cost you performance and buy you nothing you cannot already have.

Two facts settle the technical half of the question.

**Every app on your list renders through Chromium, and so does Avalonia's competitor stack.**
Claude desktop and Vortex are Electron. Steam and Spotify are CEF. Tauri on Windows is WebView2,
which is Edge, which is Chromium. Chromium draws with **Skia** and shapes text with **HarfBuzz**.
Codec's `bin` directory contains `Avalonia.Skia.dll`, `SkiaSharp.dll` and `HarfBuzzSharp.dll`.
You are already running the same rasteriser and the same text shaper as every app you admire. A
rectangle, a shadow and a glyph are drawn by the same code either way.

**Measured on your machine, right now:**

| App | Processes | Working set |
| --- | --- | --- |
| Claude desktop (Electron) | 2 | 1188 MB |
| Codec (Avalonia) | 1 | 370 MB |

The app you want Codec to feel like costs three times the memory. Electron would also add roughly
120 MB to your installer and one to three seconds of cold start. "Looks faster" is a perception,
and it is a perception you can buy without paying that.

So the gap is real, but it is not the renderer. It is three other things, in this order:
**the window, continuous feedback, and design maturity.**

## 1. The window is most of the first impression

This is the part that is genuinely, verifiably missing, and it is why Codec reads as "a program"
rather than "an app" before anyone has clicked anything.

| What a modern Windows app does | Codec today |
| --- | --- |
| Caption buttons 46x32, flush to the window edge, full titlebar height, close hovers `#C42B1C` | 30x26 rounded pills, 7px radius, inset 14px from the edge, 0.7 opacity |
| Hovering maximize shows the Windows 11 Snap Layouts flyout | Nothing. The buttons are ordinary client-area buttons, so Windows never sees `HTMAXBUTTON` |
| Close is reachable by slamming the pointer into the screen corner when maximized | The 14px inset breaks that. Fitts's law violation |
| Window remembers size, position and maximized state | Every launch is 1280x760, centred |
| Rounded corners and a tuned border colour via `DWMWA_WINDOW_CORNER_PREFERENCE` and `DWMWA_BORDER_COLOR` | Not set |
| Mica or Acrylic backdrop so the window belongs to the desktop | Flat `#1C1A17` |
| Titlebar 32 to 48px | 34px, which is fine, but the buttons inside it are not |

The caption buttons are the single loudest tell. Zoomed in they are three thin low-contrast icons
floating in space. Windows users read that instantly, even without knowing why.

**Avalonia 12 solves this properly, and Codec is not using the mechanism.** The package ships
`Avalonia.Controls.Chrome.WindowDrawnDecorations`, `IWindowDrawnDecorationsTemplate` and a
`Window.WindowDecorationsTheme` property whose documentation reads "the theme used to render the
window decorations when they are not drawn by the system". `Avalonia.Win32.dll` contains
`HTMAXBUTTON` and `WM_NCHITTEST`. That means Avalonia can own the hit-testing, and therefore
Snap Layouts, while you supply the look. Codec instead sets `WindowDecorations="BorderOnly"` and
hand-rolls three buttons in the client area, which throws that away.

`TransparencyLevelHint` accepts `Mica`, `AcrylicBlur`, `Blur`, `Transparent` and `None` as an
ordered fallback list. Mica is one attribute away.

## 2. Continuous feedback is what reads as "fast"

Chromium apps are not quicker. They are never still. CSS makes a transition nearly free, so web
developers put one on everything by default, and scrolling is animated by the engine whether you
ask or not. Until yesterday Codec had, functionally, zero working transitions: the three
animations it declared were attached to static classes on permanently mounted controls and
therefore ran once, invisibly, at startup. That is fixed. What remains:

- **Wheel scrolling still jumps.** Chromium animates the scroll offset to its target over about
  150ms with an ease-out curve. Avalonia's `ScrollViewer` moves by lines, immediately. This is
  probably the largest single remaining "cheap" signal, because scrolling is the most frequent
  thing anyone does in a library app. `ScrollViewer.IsScrollInertiaEnabled` exists but is a
  gesture feature; smooth wheel scroll needs animating `Offset` yourself.
- **Animations run on the UI thread.** Style transitions are UI-thread work, so a library scan,
  an image decode or a Steam sync makes them stutter. Avalonia ships a real compositor:
  `Avalonia.Rendering.Composition` with `Compositor`, `CompositionVisual`, `ElementComposition`,
  `ImplicitAnimationCollection`, `KeyFrameAnimation` and `ExpressionAnimation`. That is the same
  class of API as WinUI's, and it runs on the render thread. Codec uses none of it.
- **Nothing reacts to list changes.** Filter a source, sort, or search, and covers teleport.
  Chromium apps animate reflow, or at minimum cross-fade.
- **No skeletons.** Loading shows a hairline and a sentence. Chromium apps show the shape of the
  content that is coming, which reads as faster even when it is slower.

## 3. Rendering quality details Codec leaves on the table

Same Skia, different settings.

- **`RenderOptions.BitmapInterpolationMode` is never set.** Covers are 600x900 assets drawn at
  roughly 232x348. Without `HighQuality` that downscale aliases, and cover art is 90 percent of
  the pixels on screen. This is very likely visible today.
- **`RenderOptions.TextRenderingMode` is never set.** Subpixel antialiasing appears to be on, but
  it is being left to chance rather than chosen.
- **Every shadow is a single layer.** `0 8 22 0 #5C000000` on covers, one `DropShadowEffect`
  elsewhere. Real depth in modern UI is two or three stacked shadows: a tight dark contact shadow
  plus a wide soft ambient one. Avalonia's `BoxShadows` is a collection and accepts a list.
- **Elevation is nearly flat.** Four surface tokens exist; the shelf and the rail differ by an
  alpha wash. Modern apps run a deliberate three or four step elevation ramp and use it
  consistently.

## 4. What Avalonia genuinely cannot do

Being fair to your scepticism, these are real and there is no clean workaround.

- **No `backdrop-filter`.** You cannot blur what is behind an arbitrary element. Mica and Acrylic
  exist only at the window level. Frosted panels over content are out.
- **No colour matrix filters.** No `saturate()`, `contrast()`, `hue-rotate()` on a subtree.
- **No cascade.** Avalonia resolves competing style setters by application order, not
  specificity, which is why the click pulse had to be moved to the end of `Controls.axaml`
  yesterday to beat a hover rule.
- **Fewer primitives.** No layout equivalent of `aspect-ratio`, which is why `CoverGrid` exists.
  No `mix-blend-mode`. Keyframe animations cannot target `RenderTransform` directly, only the
  individual transform properties.
- **No ecosystem.** There is no Radix, no shadcn, no Tailwind. Every component is yours to build,
  which is the actual reason Electron apps look consistent: their authors did not design a
  scrollbar, they installed one.

That last point is the honest core of it. Electron apps do not look better because Chromium draws
better. They look better because a decade of web design systems set their defaults, and because
their developers inherited good taste rather than authoring it.

## 5. What to do, in impact order

**Tier 1, the window.** Highest ratio of impression to effort.
1. Move to `WindowDecorations="Full"` with a custom `WindowDecorationsTheme` so Windows sees real
   caption buttons: Snap Layouts, correct 46x32 metrics, edge-flush, red close hover.
2. Persist window size, position and maximized state in `AppSettings`.
3. Set `TransparencyLevelHint="Mica, AcrylicBlur, None"` and let the surfaces sit on it.
4. Set the corner preference and border colour through `DwmSetWindowAttribute`.

**Tier 2, motion.**
5. Smooth wheel scrolling: animate `ScrollViewer.Offset` instead of jumping.
6. Move the cover lift, the rail marker and the page crossfade onto the compositor through
   `ElementComposition` and `ImplicitAnimationCollection`, so a scan cannot stutter them.
7. Animate library reflow on filter and sort.

**Tier 3, rendering.**
8. `RenderOptions.BitmapInterpolationMode=HighQuality` on the cover surfaces.
9. Two-layer shadows on the elevation ramp.
10. Choose `TextRenderingMode` explicitly.

**Tier 4, system.**
11. A real elevation ramp, applied consistently.
12. Skeleton states for the shelf and the details page.
13. A command palette, because every app on your list has one and it is the clearest single
    signal that an app was built for people who use it daily.

Tier 1 alone would move Codec further than anything done in the last three sessions.

## Sources for the Avalonia claims

All verified against the installed packages rather than documentation:

- `avalonia/12.1.0/lib/net10.0/Avalonia.Controls.xml` for `WindowTransparencyLevel` values,
  `WindowDecorations`, `WindowDecorationsTheme`, `Chrome.WindowDrawnDecorations`,
  `ScrollViewer.IsScrollInertiaEnabled`.
- `avalonia/12.1.0/lib/net10.0/Avalonia.Base.xml` for `Avalonia.Rendering.Composition.*`,
  `RenderOptions`, `TextRenderingMode`, `BitmapInterpolationMode`, `TransitionBase.Delay`.
- `avalonia.win32/12.0.3/lib/net10.0/Avalonia.Win32.dll` string table for `DWMWA_*`,
  `DwmSetWindowAttribute`, `HTMAXBUTTON`, `WM_NCHITTEST`.
- Process memory read live from this machine.
