using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services
{
    /// <summary>
    /// Represents a discovered game candidate before validation
    /// </summary>
    public record GameCandidate(
        string Name,
        string FolderPath,
        string Source,
        int? SteamAppId = null
    );

    /// <summary>
    /// Base class for platform-specific game scanners
    /// </summary>
    public abstract class PlatformScanner
    {
        public abstract string PlatformName { get; }
        public abstract Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null);
    }

    /// <summary>
    /// Multi-layered game scanner implementing deterministic manifest parsing 
    /// with heuristic fallback for executable detection.
    /// 
    /// Implements the 3-phase "Confidence Funnel" architecture:
    /// Phase 1: High-reliability launcher integration
    /// Phase 2: Heuristic environmental scanning
    /// Phase 3: External validation and metadata enrichment
    /// </summary>
    public class GameScanner
    {
        private readonly List<PlatformScanner> _platformScanners;
        private readonly HeuristicScanner _heuristicScanner;

        public GameScanner()
        {
            _platformScanners = new List<PlatformScanner>
          {
             new SteamScanner(),
 new EpicGamesScanner(),
                new UbisoftConnectScanner(),
       new BattleNetScanner(),
  new EAAppScanner(),
           new GOGScanner()
            };
            _heuristicScanner = new HeuristicScanner();
        }

        /// <summary>
        /// Execute complete 3-phase scan
        /// </summary>
        public async Task<List<(int? SteamAppId, string GameName, int? RawgId, string ImportSource, string ExecutablePath, string FolderLocation)>> ScanAllGamesAsync(IProgress<string>? progress = null)
        {
 var allCandidates = new List<GameCandidate>();

        Debug.WriteLine("=== STARTING COMPLETE GAME LIBRARY SCAN ===");
            progress?.Report("Starting comprehensive game scan...");

         // PHASE 1: High-Reliability Launcher Integration
            Debug.WriteLine("\n=== PHASE 1: LAUNCHER INTEGRATION ===");
        foreach (var scanner in _platformScanners)
     {
  try
       {
         progress?.Report($"Scanning {scanner.PlatformName}...");
          var candidates = await scanner.ScanAsync(progress);
        allCandidates.AddRange(candidates);
          Debug.WriteLine($"✓ {scanner.PlatformName}: Found {candidates.Count} games");
    }
  catch (Exception ex)
      {
        Debug.WriteLine($"✗ {scanner.PlatformName} scan failed: {ex.Message}");
 progress?.Report($"Warning: {scanner.PlatformName} scan failed");
                }
            }

    // PHASE 2: Heuristic Environmental Scanning
            Debug.WriteLine("\n=== PHASE 2: HEURISTIC SCANNING ===");
            try
            {
  progress?.Report("Scanning standard installation directories...");
    var heuristicCandidates = await _heuristicScanner.ScanAsync(progress);
  allCandidates.AddRange(heuristicCandidates);
         Debug.WriteLine($"✓ Heuristic scan: Found {heuristicCandidates.Count} potential games");
            }
            catch (Exception ex)
{
         Debug.WriteLine($"✗ Heuristic scan failed: {ex.Message}");
            }

  // Remove duplicates based on folder path
          allCandidates = allCandidates
           .GroupBy(c => c.FolderPath, StringComparer.OrdinalIgnoreCase)
         .Select(g => g.First())
         .ToList();

            Debug.WriteLine($"\n✓ Total unique candidates: {allCandidates.Count}");

        // PHASE 3: External Validation & Enrichment + EXE Detection
 Debug.WriteLine("\n=== PHASE 3: VALIDATION & EXE DETECTION ===");
       progress?.Report($"Validating and analyzing {allCandidates.Count} candidates...");

            var validatedGames = new List<(int?, string, int?, string, string, string)>();
      int processedCount = 0;

       foreach (var candidate in allCandidates)
            {
        processedCount++;
  progress?.Report($"Validating {processedCount}/{allCandidates.Count}: {candidate.Name}");

   // Execute EXE detection funnel first
      string executablePath = ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name);

        if (string.IsNullOrEmpty(executablePath))
                {
          Debug.WriteLine($"  ✗ REJECTED: '{candidate.Name}' - No valid executable found");
    continue;
       }

    // Determine Steam ID: use existing if available, otherwise search for it
   int? steamId = candidate.SteamAppId;
      
         if (!steamId.HasValue)
         {
        // No Steam ID yet - try to find one via GameNameService (works for ANY source)
             Debug.WriteLine($"  [STEAM LOOKUP] Searching Steam for: '{candidate.Name}'");
     try
 {
              (int? foundSteamId, int? _) = await GameNameService.FindGameIdsAsync(executablePath);
             if (foundSteamId.HasValue)
     {
        steamId = foundSteamId;
 Debug.WriteLine($"  ✓ Steam ID found: {steamId} for '{candidate.Name}'");
    }
        else
 {
     Debug.WriteLine($"  ℹ No Steam ID found for '{candidate.Name}'");
       }
          }
   catch (Exception ex)
         {
       Debug.WriteLine($"  ✗ Steam lookup failed for '{candidate.Name}': {ex.Message}");
        }
         }
        else
                {
    Debug.WriteLine($"  ✓ Using existing Steam ID: {steamId} for '{candidate.Name}'");
    }

          // External validation via RAWG API
      int? rawgId = await ValidateAndFetchRawgIdAsync(candidate.Name);

                // Apply strict validation: discard if not found in RAWG (unless from Phase 1)
                bool isFromLauncher = !candidate.Source.Equals("Heuristic Scan", StringComparison.OrdinalIgnoreCase);
         if (!rawgId.HasValue && !isFromLauncher)
    {
         Debug.WriteLine($"  ✗ REJECTED: '{candidate.Name}' - Not found in RAWG database (likely not a game)");
      continue;
          }

                Debug.WriteLine($"  ✓ VALIDATED: '{candidate.Name}' (Steam ID: {steamId?.ToString() ?? "N/A"}, RAWG ID: {rawgId?.ToString() ?? "N/A"})");
   validatedGames.Add((steamId, candidate.Name, rawgId, candidate.Source, executablePath, candidate.FolderPath));
        }

            Debug.WriteLine($"\n=== SCAN COMPLETE: {validatedGames.Count} validated games ===");
      progress?.Report($"Scan complete! Found {validatedGames.Count} valid games");

   return validatedGames;
        }

        /// <summary>
        /// Validate game via RAWG API - returns null if not found (count == 0)
        /// </summary>
        private static async Task<int?> ValidateAndFetchRawgIdAsync(string gameName)
        {
            try
            {
                return await GameNameService.FindRawgIdByNameAsync(gameName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  ? RAWG validation failed: {ex.Message}");
                return null;
            }
        }

        #region LAYER 3: Heuristic Executable Detection Funnel

        /// <summary>
        /// The Detection Funnel: A step-by-step algorithm prioritizing reliability.
        /// </summary>
        private static string ExecuteDetectionFunnel(string gameFolderPath, string gameName)
        {
            if (!Directory.Exists(gameFolderPath))
            {
                Debug.WriteLine($"  ? Folder does not exist");
                return "";
            }

            Debug.WriteLine($"  === HEURISTIC FUNNEL: {gameName} ===");

            // Step 1: Recursive scan for all .exe files
            var allExeFiles = Directory.GetFiles(gameFolderPath, "*.exe", SearchOption.AllDirectories).ToList();
            Debug.WriteLine($"  [1/5] Recursive scan: {allExeFiles.Count} .exe files found");

            // Step 2: Initial exclusion (negative heuristic)
            var candidates = allExeFiles.Where(exe => !IsExcluded(exe)).ToList();
            Debug.WriteLine($"  [2/5] After exclusion: {candidates.Count} candidates remain");

            if (!candidates.Any())
            {
                Debug.WriteLine($"  ? No valid candidates");
                return "";
            }

            // Step 3: Score each candidate with weighted heuristic model
            var scoredCandidates = candidates
                      .Select(exe => new { Path = exe, Score = CalculateHeuristicScore(exe, gameName, gameFolderPath, candidates) })
       .OrderByDescending(c => c.Score)
                 .ToList();

            Debug.WriteLine($"  [3/5] Heuristic scoring complete");
            Debug.WriteLine($"  --- TOP 5 CANDIDATES ---");
            foreach (var candidate in scoredCandidates.Take(5))
            {
                Debug.WriteLine($"    [{candidate.Score:0000}] {Path.GetFileName(candidate.Path)}");
            }

            // Step 4: Advanced vector analysis (launcher detection, anti-cheat)
            var topCandidates = scoredCandidates.Take(3).Select(c => (c.Path, c.Score)).ToList();
            var finalCandidate = ApplyAdvancedVectorAnalysis(topCandidates, gameFolderPath);

            Debug.WriteLine($"  [4/5] Advanced analysis applied");
            Debug.WriteLine($"  ? SELECTED: {Path.GetFileName(finalCandidate.Path)} (Score: {finalCandidate.Score})");

            // Step 5: Return (caching happens in caller)
            return finalCandidate.Path;
        }

        private static bool IsExcluded(string exePath)
        {
            var fileName = Path.GetFileName(exePath).ToLowerInvariant();
            var directory = Path.GetDirectoryName(exePath)?.ToLowerInvariant() ?? "";

            // Excluded keywords
            var excludedKeywords = new[]
      {
  "uninstall", "setup", "redist", "dxsetup", "vcredist", "dotnet",
         "crashreporter", "activation", "support", "easyanticheat_setup",
                "eaanticheat.installer", "prereq", "directx", "battleye"
            };

            if (excludedKeywords.Any(kw => fileName.Contains(kw)))
                return true;

            // Excluded directories
            var excludedDirs = new[] { "redist", "_commonredist", "support", "directx", "prerequisites", "installers", "tools", "docs" };
            if (excludedDirs.Any(dir => directory.Contains(dir)))
                return true;

            return false;
        }

        private static int CalculateHeuristicScore(string exePath, string gameName, string gameFolderPath, List<string> allCandidates)
        {
            int score = 0;
            var fileName = Path.GetFileNameWithoutExtension(exePath);
            var fileNameLower = fileName.ToLowerInvariant();
            var gameNameLower = gameName.ToLowerInvariant();
            var directory = Path.GetDirectoryName(exePath) ?? "";
            var relativePath = exePath.Replace(gameFolderPath, "").ToLowerInvariant();
            var isRootDirectory = directory == gameFolderPath;

            // === POSITIVE FACTORS ===

            // CRITICAL PRIORITY: Launcher in Root Directory (+60)
            if (fileNameLower.Contains("launcher") && isRootDirectory)
            {
                score += 60;
            }

            // Title Match (+50): Fuzzy matching with Levenshtein distance
            double similarity = CalculateSimilarity(fileNameLower, gameNameLower);
            if (similarity > 0.8) score += 50;
            else if (similarity > 0.6) score += 30;
            else if (similarity > 0.4) score += 15;

            // Engine Build Suffix (+40): -shipping.exe (Unreal Engine)
            if (fileNameLower.EndsWith("-shipping"))
                score += 40;

            // "Launcher" Keyword (+35): Often the correct entry point
            if (fileNameLower.Contains("launcher") && !isRootDirectory)
                score += 35;

            // Architecture Match (+30): x64/win64 on 64-bit system
            if (Environment.Is64BitOperatingSystem)
            {
                bool hasX64InName = fileNameLower.Contains("x64") || fileNameLower.Contains("win64") || fileNameLower.Contains("64bit");
                bool hasX64InPath = relativePath.Contains(@"\x64\") || relativePath.Contains(@"\x64vk\") || relativePath.Contains(@"win64");

                if (hasX64InName || hasX64InPath)
                    score += 30;
            }

            // "Game" Keyword (+20)
            if (fileNameLower.Contains("game"))
                score += 20;

            // Root Directory (+10)
            if (isRootDirectory)
                score += 10;

            // Standard Engine Directory (+40)
            if (relativePath.Contains(@"\binaries\win64") || relativePath.Contains(@"\bin\") || relativePath.Contains(@"\x64\"))
                score += 40;

            // Largest File Size (+25)
            try
            {
                var thisSize = new FileInfo(exePath).Length;
                var maxSize = allCandidates.Max(c => new FileInfo(c).Length);
                if (thisSize == maxSize) score += 25;
                else if (thisSize > 50 * 1024 * 1024) score += 15;
                else if (thisSize > 10 * 1024 * 1024) score += 10;
            }
            catch { }

            // === NEGATIVE FACTORS ===

            // "Editor" Keyword (-100)
            if (fileNameLower.Contains("editor"))
                score -= 100;

            // Generic Engine Names (-20)
            var genericNames = new[] { "ue4game", "unityplayer", "unitycrashandler" };
            if (genericNames.Any(name => fileNameLower == name))
                score -= 20;

            // Render API in Path (-10)
            var renderApiMarkers = new[] { "vk", "vulkan", "gl", "opengl", "dx11", "dx12" };
            if (renderApiMarkers.Any(api => relativePath.Contains(api)))
                score -= 10;

            // Config/Update Launchers in Tools/Utils directories (-20)
            if (fileNameLower.Contains("launcher") && (relativePath.Contains("tools") || relativePath.Contains("utils") || relativePath.Contains("config")))
                score -= 20;

            return score;
        }

        private static double CalculateSimilarity(string source, string target)
        {
            int distance = LevenshteinDistance(source, target);
            int maxLength = Math.Max(source.Length, target.Length);
            return maxLength > 0 ? 1.0 - ((double)distance / maxLength) : 0.0;
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            var d = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[source.Length, target.Length];
        }

        private static (string Path, int Score) ApplyAdvancedVectorAnalysis(List<(string Path, int Score)> topCandidates, string gameFolderPath)
        {
            var candidates = topCandidates.Select(c => (c.Path, c.Score)).ToList();

            // Check for launcher pattern
            var launcherCandidate = candidates.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c.Path).ToLowerInvariant().Contains("launcher"));

            // Check for anti-cheat presence
            bool hasAntiCheat = DetectAntiCheat(gameFolderPath);

            if (hasAntiCheat && launcherCandidate != default)
            {
                Debug.WriteLine($"    ? Anti-cheat detected, prioritizing launcher");
                return launcherCandidate;
            }

            return candidates.First();
        }

        private static bool DetectAntiCheat(string gameFolderPath)
        {
            try
            {
                var subDirs = Directory.GetDirectories(gameFolderPath, "*", SearchOption.TopDirectoryOnly);
                return subDirs.Any(dir => dir.ToLowerInvariant().Contains("easyanticheat") || dir.ToLowerInvariant().Contains("battleye"));
            }
            catch { return false; }
        }

        #endregion
    }

    #region Platform Scanners

    /// <summary>
    /// Steam launcher integration - deterministic VDF parsing
    /// </summary>
    public class SteamScanner : PlatformScanner
    {
        private const string SteamLibraryFoldersPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
        private record SteamLibraryFolder(string Path, List<int> AppIds);
        private record SteamGameInfo(int AppId, string Name, string InstallDir, string LibraryPath);

        public override string PlatformName => "Steam";

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            // Parse libraryfolders.vdf
            var libraryFolders = await ParseLibraryFoldersAsync();
            if (!libraryFolders.Any())
            {
                Debug.WriteLine("? No Steam library folders found");
                return candidates;
            }

            Debug.WriteLine($"? Found {libraryFolders.Count} Steam library folders");

            // Parse app manifests
            var installedGames = new List<SteamGameInfo>();
            foreach (var folder in libraryFolders)
            {
                var gamesInFolder = await ParseAppManifestsAsync(folder.Path, folder.AppIds);
                installedGames.AddRange(gamesInFolder);
            }

            Debug.WriteLine($"? Total installed Steam games: {installedGames.Count}");

            // Convert to candidates
            foreach (var game in installedGames)
            {
                string gameFolderPath = Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);
                string source = $"Steam ({game.LibraryPath})";

                candidates.Add(new GameCandidate(game.Name, gameFolderPath, source, game.AppId));
            }

            return candidates;
        }

        private async Task<List<SteamLibraryFolder>> ParseLibraryFoldersAsync()
        {
            var folders = new List<SteamLibraryFolder>();

            if (!File.Exists(SteamLibraryFoldersPath))
            {
                return folders;
            }

            try
            {
                string vdfContent = await File.ReadAllTextAsync(SteamLibraryFoldersPath);
                VProperty vdfData = VdfConvert.Deserialize(vdfContent);

                if (vdfData.Value is VObject libraryFoldersObj)
                {
                    foreach (var folder in libraryFoldersObj)
                    {
                        if (folder.Value is VObject folderData)
                        {
                            string? folderPath = null;
                            var appIds = new List<int>();

                            foreach (var property in folderData)
                            {
                                if (property.Key == "path")
                                {
                                    folderPath = property.Value?.ToString();
                                }
                                else if (property.Key == "apps" && property.Value is VObject apps)
                                {
                                    foreach (var app in apps)
                                    {
                                        if (int.TryParse(app.Key, out int appId))
                                        {
                                            appIds.Add(appId);
                                        }
                                    }
                                }
                            }

                            if (folderPath != null && appIds.Any())
                            {
                                folders.Add(new SteamLibraryFolder(folderPath, appIds));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error parsing libraryfolders.vdf: {ex.Message}");
            }

            return folders;
        }

        private async Task<List<SteamGameInfo>> ParseAppManifestsAsync(string libraryPath, List<int> expectedAppIds)
        {
            var games = new List<SteamGameInfo>();
            string steamAppsPath = Path.Combine(libraryPath, "steamapps");

            if (!Directory.Exists(steamAppsPath))
            {
                return games;
            }

            try
            {
                var manifestFiles = Directory.GetFiles(steamAppsPath, "appmanifest_*.acf");

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        string content = await File.ReadAllTextAsync(manifestFile);
                        VProperty manifest = VdfConvert.Deserialize(content);

                        if (manifest.Value is VObject manifestData)
                        {
                            int? appId = null;
                            string? name = null;
                            string? installDir = null;

                            foreach (var property in manifestData)
                            {
                                if (property.Key == "appid" && int.TryParse(property.Value?.ToString(), out int id))
                                    appId = id;
                                else if (property.Key == "name")
                                    name = property.Value?.ToString();
                                else if (property.Key == "installdir")
                                    installDir = property.Value?.ToString();
                            }

                            if (appId.HasValue && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(installDir) && expectedAppIds.Contains(appId.Value))
                            {
                                games.Add(new SteamGameInfo(appId.Value, name, installDir, libraryPath));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"? Error parsing {Path.GetFileName(manifestFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error reading manifests: {ex.Message}");
            }

            return games;
        }
    }

    /// <summary>
    /// Epic Games Store launcher integration - JSON manifest parsing
    /// </summary>
    public class EpicGamesScanner : PlatformScanner
    {
        private const string EpicManifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

        public override string PlatformName => "Epic Games Store";

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            if (!Directory.Exists(EpicManifestsPath))
            {
                Debug.WriteLine("? Epic Games manifests directory not found");
                return candidates;
            }

            try
            {
                var manifestFiles = Directory.GetFiles(EpicManifestsPath, "*.item");

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        string jsonContent = await File.ReadAllTextAsync(manifestFile);
                        using var doc = JsonDocument.Parse(jsonContent);

                        if (doc.RootElement.TryGetProperty("InstallLocation", out var location) &&
                      doc.RootElement.TryGetProperty("DisplayName", out var displayName))
                        {
                            string installPath = location.GetString() ?? "";
                            string name = displayName.GetString() ?? "";

                            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                            {
                                candidates.Add(new GameCandidate(name, installPath, "Epic Games Store"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"? Error parsing Epic manifest {Path.GetFileName(manifestFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning Epic Games: {ex.Message}");
            }

            return candidates;
        }
    }

    /// <summary>
    /// Ubisoft Connect launcher integration - YAML parsing
    /// </summary>
    public class UbisoftConnectScanner : PlatformScanner
    {
        public override string PlatformName => "Ubisoft Connect";

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
         @"Ubisoft Game Launcher\settings.yml"
              );

            if (!File.Exists(settingsPath))
            {
                Debug.WriteLine("? Ubisoft Connect settings.yml not found");
                return candidates;
            }

            try
            {
                // Simple YAML parsing - look for game_installation_path
                string content = await File.ReadAllTextAsync(settingsPath);
                var lines = content.Split('\n');

                foreach (var line in lines)
                {
                    if (line.Contains("game_installation_path:"))
                    {
                        string path = line.Split(':')[1].Trim().Trim('"');
                        if (Directory.Exists(path))
                        {
                            // Scan this directory for game folders
                            var gameFolders = Directory.GetDirectories(path);
                            foreach (var folder in gameFolders)
                            {
                                string gameName = new DirectoryInfo(folder).Name;
                                candidates.Add(new GameCandidate(gameName, folder, "Ubisoft Connect"));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning Ubisoft Connect: {ex.Message}");
            }

            return candidates;
        }
    }

    /// <summary>
    /// Battle.net launcher integration - SQLite database query (placeholder)
    /// </summary>
    public class BattleNetScanner : PlatformScanner
    {
        public override string PlatformName => "Battle.net";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            // TODO: Implement SQLite database query for product.db
            Debug.WriteLine("Battle.net scanner not yet implemented");
            return Task.FromResult(new List<GameCandidate>());
        }
    }

    /// <summary>
    /// EA App launcher integration - Registry-based detection
    /// </summary>
    public class EAAppScanner : PlatformScanner
    {
        public override string PlatformName => "EA App";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            try
            {
                // Query registry for EA Games
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\EA Games");
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var gameKey = key.OpenSubKey(subKeyName);
                        var installDir = gameKey?.GetValue("Install Dir") as string;

                        if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                        {
                            candidates.Add(new GameCandidate(subKeyName, installDir, "EA App"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning EA App: {ex.Message}");
            }

            return Task.FromResult(candidates);
        }
    }

    /// <summary>
    /// GOG Galaxy launcher integration - Registry-based detection
    /// </summary>
    public class GOGScanner : PlatformScanner
    {
        public override string PlatformName => "GOG Galaxy";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            try
            {
                // Check registry uninstall keys
                using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");

                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        if (subKeyName.StartsWith("GOG.com", StringComparison.OrdinalIgnoreCase))
                        {
                            using var gameKey = key.OpenSubKey(subKeyName);
                            var installLocation = gameKey?.GetValue("InstallLocation") as string;
                            var displayName = gameKey?.GetValue("DisplayName") as string;

                            if (!string.IsNullOrEmpty(installLocation) &&
                              !string.IsNullOrEmpty(displayName) &&
                          Directory.Exists(installLocation))
                            {
                                candidates.Add(new GameCandidate(displayName, installLocation, "GOG Galaxy"));
                            }
                        }
                    }
                }

                // Also check default GOG Games folder
                string gogGamesPath = @"C:\GOG Games";
                if (Directory.Exists(gogGamesPath))
                {
                    foreach (var folder in Directory.GetDirectories(gogGamesPath))
                    {
                        string gameName = new DirectoryInfo(folder).Name;
                        if (!candidates.Any(c => c.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                        {
                            candidates.Add(new GameCandidate(gameName, folder, "GOG Galaxy"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning GOG Galaxy: {ex.Message}");
            }

            return Task.FromResult(candidates);
        }
    }

    /// <summary>
    /// Phase 2: Heuristic environmental scanner for platform-independent installations
    /// with multi-stage filtering
    /// </summary>
    public class HeuristicScanner : PlatformScanner
    {
        public override string PlatformName => "Heuristic Scan";

        private static readonly string[] TargetDirectories = new[]
            {
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"C:\Games",
                @"D:\",
                @"E:\",
                @"F:\"
        };

        // STAGE 1 FILTER: System, Productivity & Development Software
        private static readonly HashSet<string> DirectoryBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
  // System & Drivers
   "NVIDIA Corporation", "Intel", "AMD", "Realtek", "Common Files", "Drivers",
        "Windows Defender", "Windows NT", "WindowsPowerShell", "Microsoft",
            "Microsoft OneDrive", "Internet Explorer", "Windows Mail", "Windows Media Player",
      
   // Productivity Software
        "Adobe", "Autodesk", "LibreOffice", "7-Zip", "WinRAR", "Notepad++",
       "Microsoft Office", "VLC", "Zoom", "TeamViewer", "AnyDesk",
        
// Development Tools
       "Microsoft Visual Studio", "Python39", "Python310", "Python311", "Git",
  "Docker", "nodejs", "Java", "JetBrains", "Android Studio", "Postman",
 
       // Browsers
   "Google Chrome", "Mozilla Firefox", "Microsoft Edge", "Opera", "Brave",
      
            // Game Launchers (not games themselves)
            "Steam", "Epic Games Launcher", "GOG Galaxy", "Ubisoft Connect",
"EA App", "Battle.net", "Origin", "Xbox",
       
       // Emulatoren (selbst keine Spiele)
          "BlueStacks", "Nox", "LDPlayer", "MEmu", "Dolphin Emulator",
   "RPCS3", "Cemu", "Yuzu", "Ryujinx", "PCSX2", "ePSXe",
  
     // Typische Entwickler-Ordner
     "src", "lib", "docs", "test", "bin", "assets", "node_modules", "build",
   ".git", ".vs", "packages", "obj", "debug", "release"
      };

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            foreach (var targetDir in TargetDirectories)
            {
                if (!Directory.Exists(targetDir))
                    continue;

                try
                {
                    progress?.Report($"Scanning {targetDir}...");

                    var subDirs = Directory.GetDirectories(targetDir);

                    foreach (var dir in subDirs)
                    {
                        string dirName = new DirectoryInfo(dir).Name;

                        // STAGE 1: Apply name-based blacklist
                        if (DirectoryBlacklist.Contains(dirName))
                        {
                            Debug.WriteLine($"  [STAGE 1 REJECT] Blacklisted directory: {dirName}");
                            continue;
                        }

                        // STAGE 2: Check for developer project structure
                        if (IsDeveloperProject(dir))
                        {
                            Debug.WriteLine($"  [STAGE 2 REJECT] Developer project detected: {dirName}");
                            continue;
                        }

                        // STAGE 3: Check for emulator indicators
                        if (IsEmulator(dir))
                        {
                            Debug.WriteLine($"  [STAGE 3 REJECT] Emulator detected: {dirName}");
                            continue;
                        }

                        // STAGE 4: Must contain .exe files
                        var exeFiles = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
                        if (exeFiles.Length == 0)
                        {
                            Debug.WriteLine($"  [STAGE 4 REJECT] No .exe files found: {dirName}");
                            continue;
                        }

                        // STAGE 5: Check if predominantly non-executable files (docs/media)
                        if (IsDocumentationOrMediaFolder(dir))
                        {
                            Debug.WriteLine($"  [STAGE 5 REJECT] Documentation/Media folder: {dirName}");
                            continue;
                        }

                        // Passed all filters - add as candidate
                        Debug.WriteLine($"  [PASSED] Candidate added: {dirName}");
                        candidates.Add(new GameCandidate(dirName, dir, "Heuristic Scan"));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"✗ Error scanning {targetDir}: {ex.Message}");
                }
            }

            return await Task.FromResult(candidates);
        }

        /// <summary>
        /// STAGE 2 FILTER: Detects developer projects by folder structure
        /// </summary>
        private static bool IsDeveloperProject(string directory)
        {
            try
            {
                var subDirs = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
              .Select(d => new DirectoryInfo(d).Name.ToLowerInvariant())
                      .ToHashSet();

                // Typical dev project indicators
                var devIndicators = new[] { "src", "lib", "test", "tests", "docs", ".git", ".vs", "node_modules", "packages" };

                // If 3+ dev indicators present, likely a project
                int matchCount = devIndicators.Count(indicator => subDirs.Contains(indicator));
                return matchCount >= 3;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// STAGE 3 FILTER: Detects emulators by package names and common files
        /// </summary>
        private static bool IsEmulator(string directory)
        {
            try
            {
                var dirName = new DirectoryInfo(directory).Name.ToLowerInvariant();

                // Known emulator names
                var emulatorNames = new[]
                            {
     "bluestacks", "nox", "ldplayer", "memu", "dolphin",
  "rpcs3", "cemu", "yuzu", "ryujinx", "pcsx2", "epsxe",
       "duckstation", "retroarch", "ppsspp"
        };

                if (emulatorNames.Any(emu => dirName.Contains(emu)))
                    return true;

                // Check for Android emulator package indicators
                var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
                var fileNames = files.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToArray();

                // Android emulator indicators
                if (fileNames.Any(f => f.Contains("com.bluestacks") ||
                     f.Contains("noxplayer") ||
                          f.Contains("androidemulator")))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// STAGE 5 FILTER: Detects folders that are predominantly documentation or media
        /// </summary>
        private static bool IsDocumentationOrMediaFolder(string directory)
        {
            try
            {
                var allFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
                if (allFiles.Length == 0)
                    return false;

                // Count file types
                int docFiles = allFiles.Count(f =>
                   {
                       var ext = Path.GetExtension(f).ToLowerInvariant();
                       return ext == ".pdf" || ext == ".txt" || ext == ".docx" || ext == ".md";
                   });

                int mediaFiles = allFiles.Count(f =>
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".mp3" || ext == ".mp4" || ext == ".avi" || ext == ".mkv" || ext == ".jpg" || ext == ".png";
                    });

                int exeFiles = allFiles.Count(f => Path.GetExtension(f).ToLowerInvariant() == ".exe");

                // If >70% docs/media and <3 exe files, likely not a game
                double nonGameRatio = (double)(docFiles + mediaFiles) / allFiles.Length;
                return nonGameRatio > 0.7 && exeFiles < 3;
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion
}
