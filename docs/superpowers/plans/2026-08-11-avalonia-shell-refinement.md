# Avalonia Shell Refinement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Match the approved Codec v2 shell more closely with a fully collapsible sidebar, precise visual tokens, one Lucide icon system, and no fullscreen caption action.

**Architecture:** Keep all changes in the Avalonia presentation project. Add one view-only icon dependency, centralize visual values in the existing style dictionaries, keep sidebar state in the existing `MainViewModel`, and replace Avalonia's drawn caption controls with three narrow app-owned caption buttons while preserving the existing lifecycle and drag behavior.

**Tech Stack:** .NET 9, Avalonia UI 12.1.0 SimpleTheme, Lucide.Avalonia 0.1.64, CommunityToolkit.Mvvm, Windows x64.

## Global Constraints

- The sidebar is open by default and collapses to exactly 0 DIPs.
- Open sidebar width is 240 DIPs; content-side reopen control is 34 by 34 DIPs.
- Standard icons are 16 DIPs with 1.75 DIP strokes; sidebar toggles are 18 DIPs.
- Use only Lucide for visible action glyphs; icon types stay out of models, services, and view models.
- Preserve LibraryView and GameDetailView composition, search behavior, window lifecycle, tray behavior, and existing commands.
- Keep the AGENTS.md palette and the approved refinements: background `#1C1A17`, sidebar `#141210`, surface `#24211E`, hover `#2B2722`, border `#332E28`, primary `#E76C2B`, text `#F3EDC9`, muted text `#9E8E78`.
- Do not add or run tests. Use fresh isolated x64 builds and runtime screenshot comparisons.

---

### Task 1: Add the Lucide icon system and calibrate shared tokens

**Files:**
- Modify: `Codec.Avalonia/Codec.Avalonia.csproj`
- Modify: `Codec.Avalonia/Styles/Colors.axaml`
- Modify: `Codec.Avalonia/Styles/Typography.axaml`
- Modify: `Codec.Avalonia/Styles/Controls.axaml`
- Modify: `Codec.Avalonia/Styles/Library.axaml`

**Interfaces:**
- Consumes: existing Avalonia 12.1.0 application styles and bundled Nunito family.
- Produces: Lucide controls available to AXAML and shared sidebar/caption control classes with exact dimensions.

- [x] **Step 1: Add the view-only package reference**

Add beside the other Avalonia UI packages:

```xml
<PackageReference Include="Lucide.Avalonia" Version="0.1.64" />
```

- [x] **Step 2: Calibrate the shared color resources**

Update the existing values without adding a second palette:

```xml
<Color x:Key="CodecSidebarColor">#141210</Color>
<Color x:Key="CodecSurfaceColor">#24211E</Color>
<Color x:Key="CodecSurfaceHoverColor">#2B2722</Color>
<Color x:Key="CodecBorderColor">#332E28</Color>
```

- [x] **Step 3: Add narrow reusable shell styles**

Add classes to `Controls.axaml` for `Button.chrome`, `Button.icon-quiet`, `Button.sidebar-action`, `Button.sidebar-settings`, and `TextBox.sidebar-search`. Their exact properties are:

```xml
<Setter Property="Width" Value="34" />
<Setter Property="Height" Value="34" />
<Setter Property="MinHeight" Value="0" />
<Setter Property="Padding" Value="0" />
```

Use 8 DIP corners on sidebar actions, transparent backgrounds on quiet/icon/settings controls, and 13 DIP/500 or 600 typography according to the design spec. The search style uses `Background="Transparent"`, `BorderThickness="0,0,0,1"`, `CornerRadius="0"`, and 13 DIP regular text.

- [x] **Step 4: Tighten sidebar list typography**

In `Library.axaml`, retain the existing 34 DIP row target and set the game button to:

```xml
<Setter Property="Padding" Value="4,5" />
<Setter Property="FontSize" Value="13" />
<Setter Property="FontWeight" Value="500" />
<Setter Property="Foreground" Value="{DynamicResource CodecTextMutedBrush}" />
```

Remove the Segoe icon-font dependency from `Button.favorite` because the content becomes a Lucide control in Task 3.

- [x] **Step 5: Restore and build the package integration**

Run:

```powershell
dotnet restore Codec.Avalonia\Codec.Avalonia.csproj -p:Platform=x64
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-shell-icons\
```

Expected: restore succeeds and build reports `0 Error(s)`.

- [x] **Step 6: Commit the icon foundation**

```powershell
git add Codec.Avalonia\Codec.Avalonia.csproj Codec.Avalonia\Styles
git commit -m "style: establish Codec shell icon system"
```

---

### Task 2: Rebuild the window caption and sidebar hierarchy

**Files:**
- Modify: `Codec.Avalonia/Views/MainWindow.axaml`
- Modify: `Codec.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `Codec.Avalonia/Converters/ValueConverters.cs`

**Interfaces:**
- Consumes: `MainViewModel.IsSidebarCollapsed`, `ToggleSidebarCommand`, existing window closing lifecycle, and Lucide controls from Task 1.
- Produces: a 240 DIP open sidebar, zero-width collapsed sidebar, content-side reopen button, and custom Minimize/Maximize/Close caption actions.

- [ ] **Step 1: Make the sidebar collapse completely**

Change the converter defaults and resource values to:

```csharp
public GridLength Expanded { get; set; } = new(240);
public GridLength Collapsed { get; set; } = new(0);
```

```xml
<converters:BooleanToGridLengthConverter x:Key="SidebarWidthConverter"
                                          Expanded="240"
                                          Collapsed="0" />
```

- [ ] **Step 2: Replace the drawn caption chrome with three actions**

Set `ExtendClientAreaChromeHints="NoChrome"` on the window and keep the 34 DIP titlebar row. Put a right-aligned caption group above the drag region with Lucide `Minus`, `Square`, and `X` icons. There is no fullscreen button.

Add handlers:

```csharp
private void MinimizeWindow_Click(object? sender, RoutedEventArgs e)
    => WindowState = WindowState.Minimized;

private void MaximizeWindow_Click(object? sender, RoutedEventArgs e)
    => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

private void CloseWindow_Click(object? sender, RoutedEventArgs e)
    => Close();
```

Keep `TitleBar_PointerPressed` unchanged so drag and double-click maximize continue to work.

- [ ] **Step 3: Reorder and resize the sidebar**

Use 20 DIP horizontal insets and rows in this order:

```text
Brand + PanelLeftClose
Search icon + underline TextBox
Game list
Flexible rest area
Mascot
Add + Scan
Settings separator + Settings
```

Use Lucide `PanelLeftClose`, `Search`, `Plus`, `ScanSearch`, and `Settings` icons. Mascot height is 104 DIPs. Add and Scan are 34 DIPs high with 8 DIP spacing. Settings is a 40 DIP row beneath a one-pixel separator.

- [ ] **Step 4: Add the collapsed-state reopen action**

Place a 34 DIP quiet button inside the content surface at `Margin="14,12,0,0"`, visible only while `IsSidebarCollapsed`, using Lucide `PanelLeftOpen` and the existing `ToggleSidebarCommand`. Give it a Z-index above Library and Game Details without affecting their margins.

- [ ] **Step 5: Build and launch the shell**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-shell-layout\
```

Launch the isolated executable and verify open sidebar, zero-width collapse, reopen action, titlebar drag, minimize, maximize or restore, close flow, and absence of a fullscreen action.

- [ ] **Step 6: Commit the shell layout**

```powershell
git add Codec.Avalonia\Views\MainWindow.axaml Codec.Avalonia\Views\MainWindow.axaml.cs Codec.Avalonia\Converters\ValueConverters.cs
git commit -m "style: refine Avalonia shell and sidebar"
```

---

### Task 3: Replace remaining improvised action glyphs

**Files:**
- Modify: `Codec.Avalonia/Views/LibraryView.axaml`
- Modify: `Codec.Avalonia/Views/GameDetailView.axaml`
- Modify: `Codec.Avalonia/Views/SettingsView.axaml`
- Modify: `Codec.Avalonia/Views/FranchiseTimelineControl.axaml`
- Modify: `Codec.Avalonia/Views/Overlays/NotificationStack.axaml`

**Interfaces:**
- Consumes: existing commands, click handlers, visibility bindings, favorite color/opacity bindings, and Lucide controls.
- Produces: one consistent action-icon family across the visible Avalonia UI without changing view-model types.

- [ ] **Step 1: Replace Library favorite content**

Replace the bound font glyph with a view-owned icon while preserving favorite state bindings on the button:

```xml
<LucideIcon Kind="Star" Size="16" StrokeWidth="1.75" />
```

- [ ] **Step 2: Replace Game Details action glyphs**

Replace back, settings, chevron, close, play, and external-link glyphs with `ArrowLeft`, `Settings`, `ChevronRight`, `X`, `Play`, and `ExternalLink`. Preserve every command and click handler exactly.

- [ ] **Step 3: Replace Settings, timeline, and notification glyphs**

Use `ExternalLink` for GitHub and `X` for dismiss/close actions. Do not add icons to text-only actions that are already clear.

- [ ] **Step 4: Scan for improvised action glyphs**

Run:

```powershell
rg -n '☰|⚙|⌕|＋|×|↗|‹|›|▶' Codec.Avalonia --glob '*.axaml' --glob '!**/obj/**' --glob '!**/bin/**'
```

Expected: no remaining visible action-glyph matches. User-facing prose symbols are outside this scan's scope.

- [ ] **Step 5: Build the icon-complete UI**

Run:

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-shell-complete\
```

Expected: `0 Error(s)`.

- [ ] **Step 6: Commit icon consistency**

```powershell
git add Codec.Avalonia\Views Codec.Avalonia\Styles\Library.axaml
git commit -m "style: unify Avalonia action icons"
```

---

### Task 4: Run visual and final verification

**Files:**
- Modify if needed: only files already listed in Tasks 1-3.

**Interfaces:**
- Consumes: completed shell and icon changes.
- Produces: fresh evidence that the approved reference hierarchy, collapsed behavior, and build gate are satisfied.

- [ ] **Step 1: Capture the open sidebar state**

Launch `obj\verify-shell-complete\Codec.exe`, capture the initial Library shell, and compare brand position, search treatment, sidebar content order, mascot scale, button height, settings separator, titlebar controls, and empty-state hierarchy against the approved references.

- [ ] **Step 2: Capture the collapsed sidebar state**

Collapse the sidebar, confirm no rail remains, confirm Library or Details receives the full width, and capture the visible content-side `PanelLeftOpen` action. Reopen the sidebar and confirm state persistence still works.

- [ ] **Step 3: Apply only evidence-driven refinements**

If either screenshot violates an exact spec value, patch that existing style or AXAML value. Do not add new components or alter Library/Game Details composition.

- [ ] **Step 4: Run the final evidence gate**

```powershell
dotnet build Codec.Avalonia\Codec.Avalonia.csproj --no-restore -p:Platform=x64 -p:OutputPath=obj\verify-shell-final\
rg -n '☰|⚙|⌕|＋|×|↗|‹|›|▶' Codec.Avalonia --glob '*.axaml' --glob '!**/obj/**' --glob '!**/bin/**'
git diff --check
git status --short
```

Expected: build has `0 Error(s)`, action-glyph scan has no matches, diff check is clean, and status contains only intentional shell-refinement files.

- [ ] **Step 5: Commit any final visual calibration**

If Step 3 changed files:

```powershell
git add Codec.Avalonia
git commit -m "style: polish Avalonia shell details"
```

If no files changed, do not create an empty commit.
