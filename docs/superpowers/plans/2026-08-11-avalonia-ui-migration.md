# Codec Avalonia UI Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Codec's WinUI 3 application with a Windows-only Avalonia UI application that preserves current functionality, keeps the approved Library and Game Details layouts, adopts the Codec v2 visual language, and continues shipping through Velopack.

**Architecture:** Move UI-independent models and services into `Codec.Core`, then build `Codec.Avalonia` as the only new executable and presentation layer. Keep the old WinUI files readable as a migration reference without preserving their build, then delete them after Avalonia reaches parity.

**Tech Stack:** .NET 9 Windows x64, Avalonia 12.1.0, Avalonia.Themes.Simple 12.1.0, CommunityToolkit.Mvvm 8.4.2, SteamKit2 3.4.0, Velopack 0.0.1298, LibVLCSharp.Avalonia 3.10.0, VideoLAN.LibVLC.Windows 3.0.23.1.

## Global Constraints

- Codec remains Windows-only for this migration.
- Existing Windows integrations remain valid, including Registry access, DPAPI, Windows launcher discovery, native process launching, tray behavior, file picking, and Windows-specific storage paths.
- Existing user-facing functionality must remain available after the Avalonia cutover.
- Models and services must end the migration free of WinUI dependencies and obsolete migration scaffolding.
- The existing WinUI project is removed only after Avalonia reaches functional parity.
- Installation and updates continue through Velopack using the same product and release flow.
- The project remains English-only and uses invariant English number and date semantics where applicable.
- No new automated tests are added or run as part of this migration. Validation uses isolated builds and focused runtime checks.
- Library covers have no title captions and no Continue hero.
- The current Library and Game Details information architecture is preserved.
- Do not upgrade Velopack during the UI migration.

---

## File Structure

### Final product projects

- `Codec.Core/Codec.Core.csproj`: Windows-only models, application services, scanners, provider clients, storage, and UI-neutral helpers.
- `Codec.Avalonia/Codec.Avalonia.csproj`: executable, Avalonia views, presentation state, native UI adapters, styles, assets, and Velopack bootstrap.
- `Codec.Tests/Codec.Tests.csproj`: retained test source pointing to `Codec.Core`; tests are not run for this migration.

### Avalonia presentation boundaries

- `Codec.Avalonia/Infrastructure/IUiDispatcher.cs`: schedules view-model state changes on Avalonia's UI thread.
- `Codec.Avalonia/Infrastructure/IFilePickerService.cs`: executable and launch-script selection.
- `Codec.Avalonia/Infrastructure/IWindowService.cs`: show, hide, close, tray restore, and external URI opening.
- `Codec.Avalonia/Infrastructure/AvaloniaUiDispatcher.cs`: `Dispatcher.UIThread` adapter.
- `Codec.Avalonia/Infrastructure/AvaloniaFilePickerService.cs`: `TopLevel.StorageProvider` adapter.
- `Codec.Avalonia/Infrastructure/AvaloniaWindowService.cs`: desktop lifetime and window adapter.
- `Codec.Avalonia/Media/CodecImage.cs`: cancellation-safe local/remote image control.
- `Codec.Avalonia/Media/CodecVideoView.axaml(.cs)`: LibVLC-backed preview and overlay playback with deterministic disposal.
- `Codec.Avalonia/ViewModels/MainViewModel*.cs`: migrated application state and commands with no WinUI types.
- `Codec.Avalonia/Views/MainWindow.axaml(.cs)`: root shell, global overlays, notifications, and lifecycle wiring.
- `Codec.Avalonia/Views/LibraryView.axaml(.cs)`: current cover-only Library composition.
- `Codec.Avalonia/Views/GameDetailView.axaml(.cs)`: current Game Details composition.
- `Codec.Avalonia/Views/FranchiseTimelineControl.axaml(.cs)`: franchise overlay content.
- `Codec.Avalonia/Styles/Colors.axaml`: palette and semantic brushes.
- `Codec.Avalonia/Styles/Typography.axaml`: Nunito families and text classes.
- `Codec.Avalonia/Styles/Controls.axaml`: Codec-owned control themes and interaction states.
- `Codec.Avalonia/Styles/Surfaces.axaml`: shell, overlay, notification, and card primitives.

---

### Task 1: Extract the UI-Neutral Core

**Files:**
- Create: `Codec.Core/Codec.Core.csproj`
- Move: `Models/**` to `Codec.Core/Models/**`
- Move: `Services/**` except `Services/SystemTrayIcon.cs` to `Codec.Core/Services/**`
- Move: `Helpers/AssetUriResolver.cs`, `Helpers/PlatformSourceNames.cs`, `Helpers/RangeObservableCollection.cs`, `Helpers/RiotGameDuplicateHelper.cs`, and `Helpers/StringSimilarity.cs` to `Codec.Core/Helpers/`
- Modify: `Codec.Tests/Codec.Tests.csproj`
- Modify: `Codec.slnx`

**Interfaces:**
- Consumes: Existing namespaces under `Codec.Models`, `Codec.Services`, and `Codec.Helpers`.
- Produces: `Codec.Core.dll` with `Codec.Services.ServiceHost`, `Codec.Models.Game`, storage, scanning, provider, Steam, import, and update services.

- [ ] **Step 1: Verify exact move targets and preserve the legacy UI files**

Run:

```powershell
$repo = (Resolve-Path '.').Path
$targets = @('Models', 'Services', 'Helpers', 'Codec.Core')
$targets | ForEach-Object { Join-Path $repo $_ }
git status --short
```

Expected: every resolved path is inside the repository and the worktree contains only the approved plan document before implementation starts.

- [ ] **Step 2: Create the Core project definition**

Create `Codec.Core/Codec.Core.csproj` with the following project contract:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <RootNamespace>Codec</RootNamespace>
    <AssemblyName>Codec.Core</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="Gameloop.Vdf" Version="0.6.2" />
    <PackageReference Include="SteamKit2" Version="3.4.0" />
    <PackageReference Include="System.Drawing.Common" Version="10.0.7" />
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.9" />
    <PackageReference Include="Velopack" Version="0.0.1298" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Move the model, service, and helper sources**

Move the approved source files while retaining their existing namespaces. Leave `Services/SystemTrayIcon.cs` and all WinUI converters in the legacy tree. Do not copy sources into both product projects.

- [ ] **Step 4: Point the existing test project at Core**

Replace the project reference with:

```xml
<ProjectReference Include="..\Codec.Core\Codec.Core.csproj" />
```

Do not run the tests.

- [ ] **Step 5: Register Core in the solution**

Add these project entries to `Codec.slnx`:

```xml
<Project Path="Codec.Core\Codec.Core.csproj" />
<Project Path="Codec.Tests\Codec.Tests.csproj" />
```

Keep the legacy `Codec.csproj` entry until final cutover.

- [ ] **Step 6: Restore and build Core**

Run:

```powershell
dotnet restore Codec.Core\Codec.Core.csproj -p:Platform=x64
dotnet build Codec.Core\Codec.Core.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-core\
```

Expected: `0 Error(s)`.

- [ ] **Step 7: Prove Core is UI-framework-neutral**

Run:

```powershell
rg -n 'Microsoft\.UI|Microsoft\.WindowsAppSDK|Windows\.UI\.Xaml|Avalonia\.(Controls|Media|VisualTree)|WinRT' Codec.Core
```

Expected: no matches. Windows Registry, DPAPI, paths, and process APIs are allowed.

- [ ] **Step 8: Commit the Core extraction**

```powershell
git add Codec.Core Codec.Tests\Codec.Tests.csproj Codec.slnx Models Services Helpers
git commit -m "refactor: extract Codec core from WinUI"
```

---

### Task 2: Create the Avalonia Executable and Visual Foundation

**Files:**
- Create: `Codec.Avalonia/Codec.Avalonia.csproj`
- Create: `Codec.Avalonia/Program.cs`
- Create: `Codec.Avalonia/App.axaml`
- Create: `Codec.Avalonia/App.axaml.cs`
- Create: `Codec.Avalonia/Styles/Colors.axaml`
- Create: `Codec.Avalonia/Styles/Typography.axaml`
- Create: `Codec.Avalonia/Styles/Controls.axaml`
- Create: `Codec.Avalonia/Styles/Surfaces.axaml`
- Modify: `Codec.slnx`

**Interfaces:**
- Consumes: `Codec.Core`, bundled assets under the legacy `Assets` directory, and Velopack startup hooks.
- Produces: Windows executable assembly named `Codec`, `App.Services`, Codec theme resources, and an empty `MainWindow` host.

- [ ] **Step 1: Create the Avalonia project**

Use this project contract:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <RootNamespace>Codec</RootNamespace>
    <AssemblyName>Codec</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
    <ApplicationIcon>..\Assets\icon.ico</ApplicationIcon>
    <ApplicationManifest>..\app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia.Desktop" Version="12.1.0" />
    <PackageReference Include="Avalonia.Themes.Simple" Version="12.1.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="Velopack" Version="0.0.1298" />
    <ProjectReference Include="..\Codec.Core\Codec.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <AvaloniaResource Include="..\Assets\**\*" Link="Assets\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the Velopack-first Avalonia bootstrap**

`Program.Main` must keep Velopack ahead of Avalonia initialization:

```csharp
[STAThread]
public static void Main(string[] args)
{
    VelopackApp.Build()
        .OnBeforeUninstallFastCallback(_ => AppResetService.DeleteAppData())
        .Run();

    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}

public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
```

- [ ] **Step 3: Create application composition**

Expose the service graph from `App.axaml.cs`:

```csharp
public static ServiceHost Services { get; } = new();
```

On framework initialization, construct the Avalonia root view model and assign one `MainWindow` to `IClassicDesktopStyleApplicationLifetime.MainWindow`.

- [ ] **Step 4: Define the exact palette resources**

`Colors.axaml` must expose semantic brushes with these base values:

```xml
<Color x:Key="CodecBackgroundColor">#1C1A17</Color>
<Color x:Key="CodecSurfaceColor">#25221F</Color>
<Color x:Key="CodecSurfaceHoverColor">#2E2A25</Color>
<Color x:Key="CodecBorderColor">#3E3830</Color>
<Color x:Key="CodecPrimaryColor">#E76C2B</Color>
<Color x:Key="CodecAccentColor">#D4A832</Color>
<Color x:Key="CodecSecondaryColor">#C4956A</Color>
<Color x:Key="CodecTextColor">#F3EDC9</Color>
<Color x:Key="CodecTextMutedColor">#9E8E78</Color>
```

- [ ] **Step 5: Define typography and control ownership**

Register Nunito Regular, Medium, SemiBold, and Bold from `Assets/fonts`. Apply Codec-owned themes for `Button`, `ToggleButton`, `TextBox`, `ComboBox`, `ListBoxItem`, `ScrollBar`, `ProgressBar`, and `ToolTip`. Use 8-12 pixel standard radii, no ripple/reveal effects, and orange only for primary or selected states.

- [ ] **Step 6: Register the project and perform the first Avalonia build**

Run:

```powershell
dotnet restore Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-foundation\
```

Expected: `0 Error(s)` and an executable named `Codec.exe` in the isolated output.

- [ ] **Step 7: Commit the Avalonia foundation**

```powershell
git add Codec.Avalonia Codec.slnx
git commit -m "feat: add Avalonia application foundation"
```

---

### Task 3: Migrate Presentation State and UI Adapters

**Files:**
- Move and modify: `ViewModels/MainViewModel*.cs` to `Codec.Avalonia/ViewModels/MainViewModel*.cs`
- Move and modify: `ViewModels/LibraryViewModel.cs` and `ViewModels/GameDetailViewModel.cs`
- Create: `Codec.Avalonia/Infrastructure/IUiDispatcher.cs`
- Create: `Codec.Avalonia/Infrastructure/AvaloniaUiDispatcher.cs`
- Create: `Codec.Avalonia/Infrastructure/IFilePickerService.cs`
- Create: `Codec.Avalonia/Infrastructure/AvaloniaFilePickerService.cs`
- Create: `Codec.Avalonia/Infrastructure/IWindowService.cs`
- Create: `Codec.Avalonia/Infrastructure/AvaloniaWindowService.cs`
- Create: `Codec.Avalonia/Converters/PngBytesToBitmapConverter.cs`
- Create: `Codec.Avalonia/Converters/ValueConverters.cs`

**Interfaces:**
- Consumes: `ServiceHost`, Avalonia `Dispatcher.UIThread`, `TopLevel.StorageProvider`, and desktop lifetime.
- Produces: `MainViewModel(ServiceHost, IUiDispatcher, IFilePickerService, IWindowService)`, framework-neutral state properties, and Avalonia converters.

- [ ] **Step 1: Define the UI dispatcher contract**

```csharp
public enum UiDispatchPriority { Background, Normal }

public interface IUiDispatcher
{
    void Post(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal);
    Task InvokeAsync(Func<Task> action, UiDispatchPriority priority = UiDispatchPriority.Normal);
}
```

Map `Background` to `DispatcherPriority.Background` and `Normal` to `DispatcherPriority.Normal`.

- [ ] **Step 2: Define file and window contracts**

```csharp
public interface IFilePickerService
{
    Task<string?> PickExecutableAsync(string? initialDirectory = null);
    Task<string?> PickLaunchScriptAsync(string? initialDirectory = null);
}

public interface IWindowService
{
    void HideMainWindow();
    void ShowMainWindow();
    void CloseApplication();
    Task OpenExternalUriAsync(Uri uri);
}
```

The picker filters are `.exe` for executables and `.bat`, `.cmd`, `.lnk` for launch scripts.

- [ ] **Step 3: Move the current view-model source into Avalonia**

Retain existing public property and command names wherever bindings depend on them. Remove imports of `Microsoft.UI.*`, `Windows.Storage.Streams`, and `System.Runtime.InteropServices.WindowsRuntime`.

- [ ] **Step 4: Replace DispatcherQueue calls**

Inject `IUiDispatcher` through the `MainViewModel` constructor. Replace every `TryEnqueue` with `Post` or `InvokeAsync` while retaining current low-priority versus normal ordering.

- [ ] **Step 5: Replace WinUI QR image state**

Change:

```csharp
private ImageSource? _steamQrCode;
```

to:

```csharp
[ObservableProperty]
private byte[]? _steamQrCodePng;
```

Assign the PNG returned by QRCoder directly and convert it to an Avalonia `Bitmap` only at the binding boundary.

- [ ] **Step 6: Move picker-driven commands out of view code-behind**

The Add Game, change executable, and change launch-script commands request paths through `IFilePickerService` and then invoke the existing import or game-update methods. Cancellation returns without changing state or showing an error.

- [ ] **Step 7: Create Avalonia value converters**

Port formatting behavior for bytes, dates, percentages, durations, list joining, integer equality, null/empty fallbacks, and media type checks. Use `bool` bindings instead of returning Avalonia `Visibility` wherever Avalonia's `IsVisible` accepts the value directly.

- [ ] **Step 8: Build presentation state**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-state\
rg -n 'Microsoft\.UI|Windows\.Storage\.Streams|System\.Runtime\.InteropServices\.WindowsRuntime' Codec.Avalonia\ViewModels
```

Expected: `0 Error(s)` and no WinUI/WinRT matches in the migrated view models.

- [ ] **Step 9: Commit presentation migration**

```powershell
git add Codec.Avalonia\Infrastructure Codec.Avalonia\ViewModels Codec.Avalonia\Converters ViewModels
git commit -m "refactor: migrate presentation state to Avalonia"
```

---

### Task 4: Build the Shell and Library Surface

**Files:**
- Create: `Codec.Avalonia/Views/MainWindow.axaml`
- Create: `Codec.Avalonia/Views/MainWindow.axaml.cs`
- Create: `Codec.Avalonia/Views/LibraryView.axaml`
- Create: `Codec.Avalonia/Views/LibraryView.axaml.cs`
- Create: `Codec.Avalonia/Media/CodecImage.cs`
- Create: `Codec.Avalonia/Styles/Library.axaml`

**Interfaces:**
- Consumes: `MainViewModel.Games`, `SidebarFilteredGames`, `DisplayedGames`, `SearchText`, filters, sort commands, favorite command, and selection commands.
- Produces: responsive cover-only Library, sidebar navigation, incremental image loading, keyboard search focus, and details navigation.

- [ ] **Step 1: Build the root window geometry**

Use a two-column grid with an expanded sidebar near the current proportional width and a compact collapsed state. Keep title-bar drag regions explicit and maintain the current minimum window dimensions. The content background must separate from the sidebar through tone and spacing, not a heavy enclosing border.

- [ ] **Step 2: Build the sidebar in approved order**

Create Codec branding and collapse affordance, Search, virtualized game list, Add and Scan actions, sleeping mascot, and Settings. Bind selection activation through the existing `SelectGame` behavior; a collection refresh must not accidentally select a game.

- [ ] **Step 3: Implement cancellation-safe image loading**

`CodecImage` exposes these styled properties:

```csharp
public static readonly StyledProperty<string?> SourceProperty;
public static readonly StyledProperty<Stretch> StretchProperty;
public static readonly StyledProperty<Bitmap?> PlaceholderProperty;
```

Each source change increments a version, cancels the previous load, opens local files asynchronously, downloads HTTPS images with one shared `HttpClient`, and applies the bitmap only when the version still matches. Dispose replaced bitmaps owned by the control.

- [ ] **Step 4: Recreate the existing Library toolbar**

Preserve current sort order choices, source filters, installed/not-installed controls, game count, and total storage. Do not replace them with `All`, `Installed`, and `Recent` tabs.

- [ ] **Step 5: Create the responsive cover-only grid**

Use `ItemsRepeater` with a virtualizing uniform layout. Bind cover source, fallback artwork, favorite state, folder-size/date-added tags, opacity, and click activation. Titles are not rendered beneath covers. Covers have restrained rounding and no card border.

- [ ] **Step 6: Recreate loading and empty states**

Keep the current loading behavior. The empty state uses the sleeping mascot, `No games here yet`, and the existing concise scan/add guidance. Search with zero results preserves the current selected details game.

- [ ] **Step 7: Wire keyboard and pointer behavior**

Preserve search focus shortcuts, Back navigation, right-click sidebar collapse behavior where applicable, hover/press feedback, and favorite activation without navigating into details.

- [ ] **Step 8: Build and perform a focused runtime check**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-library\
dotnet run --project Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
```

Confirm: existing library data loads, search filters both sidebar and grid, clearing search restores results, source/install/sort controls work, favorites do not navigate, covers have no captions, and details navigation opens the selected game.

- [ ] **Step 9: Commit the shell and Library**

```powershell
git add Codec.Avalonia\Views\MainWindow* Codec.Avalonia\Views\LibraryView* Codec.Avalonia\Media\CodecImage.cs Codec.Avalonia\Styles\Library.axaml
git commit -m "feat: migrate shell and library to Avalonia"
```

---

### Task 5: Migrate Game Details and Media

**Files:**
- Create: `Codec.Avalonia/Views/GameDetailView.axaml`
- Create: `Codec.Avalonia/Views/GameDetailView.axaml.cs`
- Create: `Codec.Avalonia/Views/FranchiseTimelineControl.axaml`
- Create: `Codec.Avalonia/Views/FranchiseTimelineControl.axaml.cs`
- Create: `Codec.Avalonia/Media/CodecVideoView.axaml`
- Create: `Codec.Avalonia/Media/CodecVideoView.axaml.cs`
- Create: `Codec.Avalonia/Styles/GameDetails.axaml`
- Modify: `Codec.Avalonia/Codec.Avalonia.csproj`

**Interfaces:**
- Consumes: `SelectedGame`, launch commands, media/franchise collections, taxonomy rows, game settings state, file picker service, and external URI service.
- Produces: current Game Details information architecture, aspect-ratio-safe hero artwork, launch feedback, media playback, franchise overlay, settings, and removal confirmation.

- [ ] **Step 1: Add the Windows media dependencies**

Add:

```xml
<PackageReference Include="LibVLCSharp.Avalonia" Version="3.10.0" />
<PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.0.23.1" />
```

These packages are confined to `Codec.Avalonia`; `Codec.Core` remains media-framework-neutral.

- [ ] **Step 2: Recreate the current detail composition**

Port the hero, back button, Play and Settings actions, summary metadata, developer/publisher/age/price column, description, separate Genres/Themes/Game modes rows, media rail, external links, and franchise access. Preserve content order and compactness.

- [ ] **Step 3: Preserve hero artwork rules**

Use `Stretch="Uniform"` for the foreground artwork and retain full vertical image visibility. Landscape hero backdrops may fill their layer, but the special logo-less artwork case must never crop the bottom. Do not render title text when the logo is absent.

- [ ] **Step 4: Recreate launch and settings behavior**

Bind Play to `PlaySelectedGameCommand`, preserve launch/install spinner timing and disabled state, and route executable/script changes through `IFilePickerService`. Keep remove-game confirmation inside Codec rather than using a stock dialog.

- [ ] **Step 5: Implement deterministic video lifetime**

`CodecVideoView` owns one `LibVLC`, one `MediaPlayer`, and at most one current `Media`. Its public API is:

```csharp
public Uri? Source { get; set; }
public bool AutoPlay { get; set; }
public void Play();
public void Pause();
public void Stop();
```

Changing `Source` disposes the previous `Media`. Closing or changing the media overlay calls `Stop`. Detaching the control disposes the player and LibVLC instance.

- [ ] **Step 6: Migrate media and franchise overlays**

Images use `CodecImage`; videos use `CodecVideoView`. Selecting a thumbnail opens the matching index. Closing the overlay pauses/stops every video. Franchise filtering retains Mainline, Extended, and All counts and current timeline ordering.

- [ ] **Step 7: Build and perform a focused runtime check**

Run:

```powershell
dotnet restore Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-details\
dotnet run --project Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
```

Confirm: back navigation, play/install, settings, path pickers, hero aspect ratio, separate taxonomy rows, external links, media selection/playback/close, franchise filters, and remove confirmation.

- [ ] **Step 8: Commit Game Details**

```powershell
git add Codec.Avalonia\Views\GameDetailView* Codec.Avalonia\Views\FranchiseTimelineControl* Codec.Avalonia\Media\CodecVideoView* Codec.Avalonia\Styles\GameDetails.axaml Codec.Avalonia\Codec.Avalonia.csproj
git commit -m "feat: migrate game details to Avalonia"
```

---

### Task 6: Migrate Onboarding, Settings, Steam, and Global Overlays

**Files:**
- Create: `Codec.Avalonia/Views/OnboardingView.axaml`
- Create: `Codec.Avalonia/Views/OnboardingView.axaml.cs`
- Create: `Codec.Avalonia/Views/SettingsView.axaml`
- Create: `Codec.Avalonia/Views/SettingsView.axaml.cs`
- Create: `Codec.Avalonia/Views/Overlays/NotificationStack.axaml`
- Create: `Codec.Avalonia/Views/Overlays/NotificationStack.axaml.cs`
- Modify: `Codec.Avalonia/Views/MainWindow.axaml`
- Modify: `Codec.Avalonia/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: onboarding, scan/import, Steam, update, reset, settings, and notification state already exposed by `MainViewModel`.
- Produces: approved three-stage onboarding, settings overlay, Steam QR/sign-in flow, quiet progress, update feedback, and in-app confirmations.

- [ ] **Step 1: Build the onboarding frame**

Use a full-window surface without the Library shell. The first step contains `A quiet home for your games.`, the approved supporting copy, `Begin`, and `Skip`. The final step contains exactly `Your shelf is ready.` and `Open library`. Use the mascot and a minimal three-position progress indicator.

- [ ] **Step 2: Connect onboarding to existing behavior**

`Begin` advances through setup and existing scan/Steam choices. `Skip` uses the existing no-scan path. `Open library` calls the existing completion command and does not create a second initialization route.

- [ ] **Step 3: Build Settings as an in-app overlay**

Preserve scan-on-startup, Steam launch behavior, close-to-tray choice, Steam connection/sync/disconnect, update check, cover refresh, reset, debug-only actions, app version, and external project links. Use one clear panel and short section labels rather than nested card stacks.

- [ ] **Step 4: Bind Steam QR and progress**

Convert `SteamQrCodePng` to an Avalonia bitmap. Preserve immediate connection feedback, cancel/disconnect behavior, and the compact progress contract: one activity label and one progress value.

- [ ] **Step 5: Port notifications and confirmations**

Migrate update, scan complete, already added, not added, admin warning, and failure notifications to a shared bottom-right stack. Reset, close choice, remove game, and destructive actions use in-app overlays with a dimmed backdrop and safe cancellation.

- [ ] **Step 6: Preserve overlay layering and dismissal**

Global notifications render beneath modal overlays. Back/Escape closes the top transient layer before navigating. Clicking a scrim dismisses only overlays that are currently light-dismissible.

- [ ] **Step 7: Build and perform a focused runtime check**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-overlays\
dotnet run --project Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
```

Confirm onboarding navigation, scan choice, settings open/close, Steam QR, Steam cancellation/disconnect, progress copy, notification placement, reset confirmation, and Escape/back layering.

- [ ] **Step 8: Commit onboarding and overlays**

```powershell
git add Codec.Avalonia\Views\OnboardingView* Codec.Avalonia\Views\SettingsView* Codec.Avalonia\Views\Overlays Codec.Avalonia\Views\MainWindow*
git commit -m "feat: migrate onboarding and overlays to Avalonia"
```

---

### Task 7: Complete Window, Tray, Close, and Update Lifecycle

**Files:**
- Create: `Codec.Avalonia/Infrastructure/TrayService.cs`
- Modify: `Codec.Avalonia/Infrastructure/AvaloniaWindowService.cs`
- Modify: `Codec.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `Codec.Avalonia/App.axaml.cs`
- Modify: `Codec.Avalonia/Program.cs`
- Modify: `Codec.Avalonia/Codec.Avalonia.csproj`

**Interfaces:**
- Consumes: `CloseToTray`, `HasCloseBehaviorChoice`, update service state, Avalonia desktop lifetime, and the bundled icon.
- Produces: minimum-size window behavior, close-choice overlay, hide/restore tray lifecycle, final shutdown, and Velopack update restart.

- [ ] **Step 1: Set the main-window contract**

Apply the existing minimum client dimensions, dark title-bar treatment, icon, centered first launch, and normal resize behavior. Avoid custom window chrome that interferes with Windows snapping or system controls.

- [ ] **Step 2: Implement tray behavior with Avalonia APIs**

Create one `TrayIcon` with the Codec icon, Open Codec, and Exit actions. Open restores and activates the existing window. Exit bypasses close-to-tray and shuts down the desktop lifetime exactly once.

- [ ] **Step 3: Implement the close decision flow**

On window close:

- If a final shutdown is already requested, allow close.
- If `CloseToTray == true`, cancel and hide.
- If `CloseToTray == false`, allow close.
- If no choice exists, cancel once and open the in-app close-choice overlay.

Persist the selected behavior through the existing settings service.

- [ ] **Step 4: Complete update lifecycle**

Run update checking after the main service graph exists, preserve existing progress state, and route restart/apply through the existing `UpdateService`. Keep `VelopackApp.Build().Run()` as the first application startup operation.

- [ ] **Step 5: Build and perform a focused runtime check**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-lifecycle\
dotnet run --project Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
```

Confirm first-close choice, hide, tray restore, tray exit, direct close, minimum sizing, and update-state presentation.

- [ ] **Step 6: Commit lifecycle integration**

```powershell
git add Codec.Avalonia\Infrastructure Codec.Avalonia\Views\MainWindow.axaml.cs Codec.Avalonia\App.axaml.cs Codec.Avalonia\Program.cs Codec.Avalonia\Codec.Avalonia.csproj
git commit -m "feat: complete Avalonia desktop lifecycle"
```

---

### Task 8: Audit Functional Parity and Visual Fidelity

**Files:**
- Modify: `Codec.Avalonia/Views/*.axaml`
- Modify: `Codec.Avalonia/Views/*.axaml.cs`
- Modify: `Codec.Avalonia/Views/Overlays/*.axaml`
- Modify: `Codec.Avalonia/Styles/*.axaml`
- Modify: `Codec.Avalonia/ViewModels/MainViewModel*.cs`
- Create: `docs/superpowers/verification/2026-08-11-avalonia-parity.md`

**Interfaces:**
- Consumes: every migrated surface and command.
- Produces: a checked parity record and a visually coherent Avalonia candidate ready for cutover.

- [ ] **Step 1: Record the parity matrix**

Create the verification document with explicit rows for startup, onboarding, library loading, sidebar search, grid search, sorting, source filter, install filter, favorite, Add, Scan, cancellation, Details, Play, install request, paths, remove, media, franchise, settings, Steam connect/sync/disconnect, achievements maintenance, notifications, reset, tray, close choice, update check, and update restart.

- [ ] **Step 2: Run one bounded visual inspection pass**

Capture Library, Game Details, empty state, onboarding first/final steps, Settings, Steam QR, and one modal overlay at the reference window size. Compare typography, spacing, alignment, border use, radii, colors, and control order against the approved references and current WinUI layouts.

- [ ] **Step 3: Fix all issues found in the first pass**

Apply one grouped correction pass. Preserve Library cover-only presentation, current toolbar organization, current Game Details composition, and original hero-artwork behavior.

- [ ] **Step 4: Run the second and final visual confirmation pass**

Recapture the affected surfaces once. Record the resolved findings in the parity document; do not start an open-ended screenshot loop.

- [ ] **Step 5: Run the complete focused behavior check**

Use existing persisted library data and verify every parity-matrix row manually. Record Pass, Not Applicable, or a concrete blocking observation. Do not record a feature as passing without exercising it.

- [ ] **Step 6: Build and scan dependencies**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-avalonia-parity\
rg -n 'Microsoft\.UI|Microsoft\.WindowsAppSDK|Windows\.UI\.Xaml|WinRT' Codec.Core Codec.Avalonia
```

Expected: `0 Error(s)` and no WinUI dependency matches in the product projects.

- [ ] **Step 7: Commit parity corrections**

```powershell
git add Codec.Avalonia docs\superpowers\verification\2026-08-11-avalonia-parity.md
git commit -m "fix: complete Avalonia feature parity"
```

---

### Task 9: Cut Over and Remove WinUI

**Files:**
- Remove: root `Codec.csproj`, `App.xaml`, `App.xaml.cs`, `Program.cs`
- Remove: legacy `Views/**`
- Remove: remaining WinUI-only `Helpers/**`
- Remove: `Services/SystemTrayIcon.cs`
- Move: `Assets/**` to `Codec.Avalonia/Assets/**`
- Move: `app.manifest` to `Codec.Avalonia/app.manifest`
- Modify: `Codec.Avalonia/Codec.Avalonia.csproj`
- Modify: `Codec.slnx`
- Modify: `README.md`

**Interfaces:**
- Consumes: parity-approved `Codec.Core` and `Codec.Avalonia`.
- Produces: final solution with no WinUI project or Microsoft.WindowsAppSDK dependency and `Codec.exe` supplied by Avalonia.

- [ ] **Step 1: Verify destructive targets before removal**

Run:

```powershell
$repo = (Resolve-Path '.').Path
$targets = @('Codec.csproj', 'App.xaml', 'App.xaml.cs', 'Program.cs', 'Views', 'Helpers', 'Services\SystemTrayIcon.cs')
$targets | ForEach-Object {
    $resolved = Join-Path $repo $_
    if (-not $resolved.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Target escaped repository: $resolved" }
    $resolved
}
git status --short
```

Continue only when all targets resolve inside the repository and the parity commit exists.

- [ ] **Step 2: Move final assets into Avalonia**

Move the icon, mascot, placeholders, and Nunito font assets into `Codec.Avalonia/Assets`. Update `AvaloniaResource`, `ApplicationIcon`, and manifest paths to local project-relative paths.

- [ ] **Step 3: Remove legacy WinUI files**

Remove only the verified legacy project, WinUI views, WinUI converters, and obsolete native tray implementation. Preserve `.git`, `Codec.Core`, `Codec.Avalonia`, tests, documentation, assets now owned by Avalonia, and release metadata.

- [ ] **Step 4: Simplify the solution**

The final `Codec.slnx` contains:

```xml
<Project Path="Codec.Core\Codec.Core.csproj" />
<Project Path="Codec.Avalonia\Codec.Avalonia.csproj" />
<Project Path="Codec.Tests\Codec.Tests.csproj" />
```

Remove WinUI platform/deploy mappings that no longer apply.

- [ ] **Step 5: Update project documentation**

Describe Codec as a Windows Avalonia desktop application, retain the existing purpose and feature description, and document `Codec.Avalonia/Codec.Avalonia.csproj` as the executable project.

- [ ] **Step 6: Restore and run the final isolated build**

Run:

```powershell
dotnet restore Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-final\
```

Expected: `0 Error(s)`.

- [ ] **Step 7: Verify the final dependency boundary**

Run:

```powershell
rg -n 'Microsoft\.UI|Microsoft\.WindowsAppSDK|UseWinUI|WinRT' Codec.Core Codec.Avalonia Codec.slnx README.md
```

Expected: no matches.

- [ ] **Step 8: Commit the WinUI removal**

```powershell
git add -A
git commit -m "refactor: replace WinUI with Avalonia"
```

---

### Task 10: Verify Publish and Velopack Packaging Path

**Files:**
- Create: `scripts/package.ps1`
- Modify: `docs/superpowers/verification/2026-08-11-avalonia-parity.md`

**Interfaces:**
- Consumes: final `Codec.Avalonia` executable with `AssemblyName=Codec` and Velopack 0.0.1298 startup integration.
- Produces: publish output and packaging inputs that continue to identify `Codec.exe` as the main executable.

- [ ] **Step 1: Add a repeatable packaging script for the established Codec identity**

Create `scripts/package.ps1` with this contract:

```powershell
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([+-][0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$repo = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repo 'obj\publish-avalonia'
$releases = Join-Path $repo 'obj\velopack-releases'

dotnet publish (Join-Path $repo 'Codec.Avalonia\Codec.Avalonia.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

vpk pack --packId Codec --packVersion $Version --packDir $publish `
    --mainExe Codec.exe --packTitle Codec `
    --icon (Join-Path $repo 'Codec.Avalonia\Assets\icon.ico') `
    --outputDir $releases
exit $LASTEXITCODE
```

This keeps `Codec` as pack ID, title, and executable identity and does not change channel, feed, or signing behavior.

- [ ] **Step 2: Publish the Avalonia executable**

Run:

```powershell
dotnet publish Codec.Avalonia\Codec.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false -o obj\publish-avalonia\
```

Expected: `obj\publish-avalonia\Codec.exe` exists with its Avalonia, native, and media dependencies.

- [ ] **Step 3: Exercise non-destructive Velopack startup hooks from publish output**

Run:

```powershell
& 'obj\publish-avalonia\Codec.exe' --veloapp-install 0.8.0
& 'obj\publish-avalonia\Codec.exe' --veloapp-updated 0.8.0
```

Expected: each hook exits promptly without opening the Avalonia window. Do not invoke `--veloapp-uninstall` against the user's profile because the existing uninstall hook deletes Codec app data; verify that hook by code inspection.

- [ ] **Step 4: Run the established Velopack packaging command against the new output**

Run:

```powershell
.\scripts\package.ps1 -Version 0.8.0
```

Expected: packages are written under `obj\velopack-releases` using `Codec.exe` as `--mainExe`. Do not change product identity or upgrade the Velopack CLI/package during this verification.

- [ ] **Step 5: Install and launch the generated package**

Confirm the installed app starts the Avalonia UI, loads existing data, checks for updates without blocking startup, and retains the stable Codec shortcut/executable identity.

- [ ] **Step 6: Record packaging evidence and commit**

Add the publish command, package command, generated installer filename, hook results, and installed-launch result to the parity document, then run:

```powershell
git add scripts\package.ps1 README.md docs\superpowers\verification\2026-08-11-avalonia-parity.md
git commit -m "chore: migrate Codec packaging to Avalonia"
```

---

## Final Verification Gate

Before claiming completion, run `superpowers:verification-before-completion` and verify fresh evidence for:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-final-gate\
rg -n 'Microsoft\.UI|Microsoft\.WindowsAppSDK|UseWinUI|WinRT' Codec.Core Codec.Avalonia Codec.slnx README.md
git status --short
```

Completion requires `0 Error(s)`, no WinUI dependency matches, documented runtime parity, documented Velopack packaging evidence, and only intentional worktree changes.
