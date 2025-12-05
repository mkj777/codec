# GameScanService Refactoring - Implementation Summary

## Overview
The `GameScanService.cs` has been completely refactored to implement the comprehensive 3-phase "Confidence Funnel" architecture as outlined in the technical guidelines. This refactoring transforms the service from a single-platform (Steam-only) scanner into a robust, multi-platform game detection system.

## Architecture Changes

### 1. Three-Phase "Confidence Funnel" Architecture

The new implementation follows the architectural blueprint exactly as specified in the guidelines:

#### **Phase 1: High-Reliability Launcher Integration**
- **Purpose**: Deterministic parsing of structured launcher metadata
- **Implemented Platforms**:
  - **Steam**: VDF parsing of `libraryfolders.vdf` and `appmanifest_*.acf` files
  - **Epic Games Store**: JSON parsing of `.item` manifest files from `C:\ProgramData\Epic\...`
  - **Ubisoft Connect**: YAML parsing of `settings.yml`
  - **EA App**: Registry-based detection via `HKLM\SOFTWARE\WOW6432Node\EA Games`
  - **GOG Galaxy**: Registry uninstall keys + default folder scanning
  - **Battle.net**: Placeholder for SQLite `product.db` query (future implementation)

- **Key Principle**: "Local-First" strategy - prioritizes local configuration files over web APIs for stability and resilience

#### **Phase 2: Heuristic Environmental Scanning**
- **Purpose**: Identify platform-independent game installations
- **Target Directories**: 
  - `C:\Program Files`
  - `C:\Program Files (x86)`
  - `C:\Games`, `D:\Games`, `E:\Games`
  - `C:\Spiele`, `D:\Spiele` (German locale support)

- **Blacklist Filtering**: Implements comprehensive exclusion list for:
  - System components (NVIDIA, Intel, AMD, Windows Defender, etc.)
  - Productivity software (Adobe, Autodesk, LibreOffice, 7-Zip)
  - Development tools (Visual Studio, Python, Git, Docker, nodejs)
  - Development directories (`src`, `lib`, `docs`, `test`, `node_modules`, etc.)

#### **Phase 3: External Validation & Enrichment + EXE Detection**
- **RAWG API Validation**: Each candidate is validated against the RAWG.io database
  - **Strict Validation**: Candidates from Phase 2 (Heuristic Scan) are rejected if not found in RAWG (count == 0)
  - **Lenient Validation**: Candidates from Phase 1 (Launcher Integration) proceed even without RAWG validation
  - **Purpose**: Filters false positives (non-game software) and normalizes game names

- **EXE Detection Funnel**: 5-step weighted heuristic algorithm applied to each validated candidate (unchanged from original implementation)

### 2. Modular Design Pattern

```csharp
public abstract class PlatformScanner
{
    public abstract string PlatformName { get; }
    public abstract Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null);
}
```

**Benefits**:
- Each launcher has its own self-contained scanner class
- Easy to add new platforms (implement `PlatformScanner` base class)
- Independent error handling per platform
- Follows Single Responsibility Principle

**Implemented Scanners**:
1. `SteamScanner`
2. `EpicGamesScanner`
3. `UbisoftConnectScanner`
4. `BattleNetScanner` (placeholder)
5. `EAAppScanner`
6. `GOGScanner`
7. `HeuristicScanner`

### 3. GameCandidate Record

```csharp
public record GameCandidate(
    string Name,
    string FolderPath,
    string Source,
    int? SteamAppId = null
);
```

- Intermediate representation of discovered games before validation
- Allows Phase 1 and Phase 2 to produce uniform output
- Facilitates deduplication across sources

### 4. Main Scanner Orchestration

The `GameScanner` class orchestrates the entire process:

```csharp
public async Task<List<(int? SteamAppId, string GameName, int? RawgId, string ImportSource, string ExecutablePath, string FolderLocation)>> ScanAllGamesAsync(IProgress<string>? progress = null)
```

**Process Flow**:
1. Initialize all platform scanners
2. Execute Phase 1 (all launcher scanners in sequence)
3. Execute Phase 2 (heuristic scanner)
4. Deduplicate candidates by folder path
5. Execute Phase 3 for each candidate:
   - RAWG API validation
   - EXE detection funnel
   - Rejection or acceptance based on validation results
6. Return validated games list

## Technical Implementation Details

### Steam Scanner
- **Method**: VDF parsing using `Gameloop.Vdf` library
- **Files Parsed**:
  - `libraryfolders.vdf`: Lists all Steam library locations and associated app IDs
  - `appmanifest_<appid>.acf`: Contains game metadata (name, installdir)
- **Reliability**: Very High (deterministic, structured data)

### Epic Games Scanner
- **Method**: JSON parsing
- **Files Parsed**: `*.item` files in `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\`
- **Data Extracted**: `InstallLocation`, `DisplayName`
- **Reliability**: Very High

### Ubisoft Connect Scanner
- **Method**: Simple YAML parsing
- **Files Parsed**: `%LOCALAPPDATA%\Ubisoft Game Launcher\settings.yml`
- **Implementation**: Line-by-line search for `game_installation_path:`
- **Reliability**: High (configuration-based, no authentication required)

### EA App Scanner
- **Method**: Windows Registry querying
- **Registry Path**: `HKLM\SOFTWARE\WOW6432Node\EA Games\[GameName]`
- **Data Extracted**: `Install Dir` value
- **Reliability**: High (stable API post-Origin migration)

### GOG Galaxy Scanner
- **Method**: Dual approach
  1. Registry: `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\GOG.com*`
  2. Default folder: `C:\GOG Games`
- **Data Extracted**: `InstallLocation`, `DisplayName`
- **Reliability**: Medium-High (handles non-Galaxy installations)

### Heuristic Scanner
- **Method**: Filesystem traversal with blacklist filtering
- **Validation**: Directory must contain at least one `.exe` file
- **Depth**: 1 level (immediate subdirectories only)
- **Reliability**: Medium (requires Phase 3 validation)

## Backward Compatibility

### API Change
- **Old**: `GameScanner.ScanSteamLibraryAsync(progress)` (static method)
- **New**: `new GameScanner().ScanAllGamesAsync(progress)` (instance method)

### Updated Files
- `MainWindow.xaml.cs`: Updated to instantiate `GameScanner` and call `ScanAllGamesAsync()`
- Return type remains unchanged: `List<(int? SteamAppId, string GameName, int? RawgId, string ImportSource, string ExecutablePath, string FolderLocation)>`

## Alignment with Technical Guidelines

| Guideline Section | Implementation Status | Details |
|-------------------|----------------------|---------|
| Phase 1: Launcher Integration | ? Complete (6 platforms) | Steam, Epic, Ubisoft, EA, GOG, Battle.net (placeholder) |
| Phase 2: Heuristic Scanning | ? Complete | Blacklist filtering, multi-drive support |
| Phase 3: RAWG Validation | ? Complete | Strict validation for heuristic candidates |
| EXE Detection Funnel | ? Preserved | Original 5-step algorithm maintained |
| Modular Architecture | ? Complete | Abstract base class pattern |
| Local-First Strategy | ? Complete | No API dependencies for core detection |
| Error Resilience | ? Complete | Per-scanner try-catch blocks |

## EXE Detection Funnel (Unchanged)

The original sophisticated EXE detection algorithm has been preserved:

### 5-Step Process:
1. **Recursive Scan**: Find all `.exe` files in game directory
2. **Initial Exclusion**: Remove installers, redistributables, crash reporters
3. **Weighted Scoring**: Assign scores based on:
   - Launcher in root directory (+60)
   - Title match via Levenshtein distance (+50)
   - Engine build suffix (-shipping) (+40)
   - Architecture match (x64) (+30)
   - File size (largest = best) (+25)
   - Negative markers (editor: -100, generic engines: -20)
4. **Advanced Vector Analysis**: Anti-cheat detection (prioritizes launchers if EAC/BattlEye present)
5. **Selection**: Return highest-scoring candidate

## Future Enhancements (Roadmap)

1. **Battle.net SQLite Integration**: Implement `product.db` query using SQLite library
2. **itch.io Scanner**: Add support for itch.io app installations
3. **Xbox Game Pass Scanner**: Windows Store/Xbox app integration
4. **Cache Layer**: Implement local database to avoid re-scanning unchanged directories
5. **Parallel Scanning**: Execute platform scanners concurrently for improved performance
6. **User Configuration**: Allow users to add custom scan directories
7. **Metadata Enrichment**: Expand RAWG API usage to fetch cover art, descriptions, genres

## Testing Recommendations

1. **Unit Tests**: Create tests for each `PlatformScanner` implementation
2. **Integration Tests**: Test complete scan workflow with mock file systems
3. **Performance Tests**: Measure scan time for large libraries (1000+ games)
4. **Edge Cases**: 
   - Missing launcher configurations
   - Symlinks and junction points
   - Multi-library setups (e.g., games on NAS drives)
   - Non-English game names
5. **RAWG API Fallback**: Test behavior when API is unavailable or rate-limited

## Debug Output Example

```
=== STARTING COMPLETE GAME LIBRARY SCAN ===

=== PHASE 1: LAUNCHER INTEGRATION ===
? Steam: Found 47 games
? Epic Games Store: Found 12 games
? Ubisoft Connect: settings.yml not found
? EA App: Found 3 games
? GOG Galaxy: Found 8 games
Battle.net scanner not yet implemented

=== PHASE 2: HEURISTIC SCANNING ===
? Heuristic scan: Found 23 potential games

? Total unique candidates: 91

=== PHASE 3: VALIDATION & EXE DETECTION ===
  ? VALIDATED: 'Cyberpunk 2077' (RAWG ID: 41494)
  === HEURISTIC FUNNEL: Cyberpunk 2077 ===
  [1/5] Recursive scan: 38 .exe files found
  [2/5] After exclusion: 12 candidates remain
  [3/5] Heuristic scoring complete
  --- TOP 5 CANDIDATES ---
    [0145] Cyberpunk2077.exe
    [0095] REDprelauncher.exe
    [0060] Cyberpunk2077.exe (x86)
  [4/5] Advanced analysis applied
  ? SELECTED: Cyberpunk2077.exe (Score: 145)

=== SCAN COMPLETE: 83 validated games ===
```

## Conclusion

This refactoring transforms the `GameScanService` into a professional-grade, multi-platform game detection system that follows industry best practices and adheres strictly to the technical architecture outlined in the guidelines. The implementation is extensible, maintainable, and resilient to platform-specific failures.

The new architecture provides a solid foundation for future enhancements and positions the Codec application to compete with established game library managers like Playnite and GOG Galaxy 2.0.
