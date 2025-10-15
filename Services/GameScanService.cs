using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.Services
{
    /// <summary>
    /// Multi-layered game scanner implementing deterministic manifest parsing 
    /// with heuristic fallback for executable detection.
    /// </summary>
    public class GameScanner
    {
        private const string SteamLibraryFoldersPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";

        private record SteamLibraryFolder(string Path, List<int> AppIds);
        private record SteamGameInfo(int AppId, string Name, string InstallDir, string LibraryPath);

        public static async Task<List<(int SteamAppId, string GameName, int? RawgId, string ImportSource, string ExecutablePath, string FolderLocation)>> ScanSteamLibraryAsync(IProgress<string>? progress = null)
        {
            var games = new List<(int, string, int?, string, string, string)>();

            Debug.WriteLine("=== STEAM LIBRARY SCAN: DETERMINISTIC + HEURISTIC APPROACH ===");
            progress?.Report("Reading Steam library configuration...");

            try
            {
                // LAYER 1: DETERMINISTIC - Parse libraryfolders.vdf for Steam IDs and library paths
                var libraryFolders = await ParseLibraryFoldersAsync();
                if (!libraryFolders.Any())
                {
                    Debug.WriteLine("No Steam library folders found");
                    progress?.Report("No Steam library folders found!");
                    return games;
                }

                Debug.WriteLine($"✓ Found {libraryFolders.Count} library folders");

                // LAYER 2: DETERMINISTIC - Parse appmanifest_*.acf files for InstallDir
                var installedGames = new List<SteamGameInfo>();
                foreach (var folder in libraryFolders)
                {
                    progress?.Report($"Scanning library: {folder.Path}");
                    var gamesInFolder = await ParseAppManifestsAsync(folder.Path, folder.AppIds);
                    installedGames.AddRange(gamesInFolder);
                }

                Debug.WriteLine($"✓ Total installed games: {installedGames.Count}");
                progress?.Report($"Found {installedGames.Count} installed Steam games");

                // LAYER 3: HEURISTIC - Find best executable for each game
                int processedCount = 0;
                foreach (var gameInfo in installedGames.OrderBy(g => g.AppId))
                {
                    processedCount++;
                    progress?.Report($"Analyzing {processedCount}/{installedGames.Count}: {gameInfo.Name}");

                    string gameFolderPath = Path.Combine(gameInfo.LibraryPath, "steamapps", "common", gameInfo.InstallDir);

                    // Execute heuristic detection trichter (funnel)
                    string executablePath = ExecuteDetectionFunnel(gameFolderPath, gameInfo.Name);

                    Debug.WriteLine($"\nGame: {gameInfo.Name} (AppID: {gameInfo.AppId})");
                    Debug.WriteLine($"  Install: {gameFolderPath}");
                    Debug.WriteLine($"  Executable: {executablePath ?? "[NOT FOUND]"}");

                    // Fetch RAWG ID
                    int? rawgId = await FetchRawgIdAsync(gameInfo.Name, progress);

                    string importSource = $"Steam ({gameInfo.LibraryPath})";
                    games.Add((gameInfo.AppId, gameInfo.Name, rawgId, importSource, executablePath, gameFolderPath));
                }

                progress?.Report($"Completed! Processed {installedGames.Count} games");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR: {ex.Message}");
                Debug.WriteLine($"Stack: {ex.StackTrace}");
                progress?.Report($"Error: {ex.Message}");
            }

            Debug.WriteLine($"\n=== SCAN COMPLETE: {games.Count} games processed ===");
            return games;
        }

        #region LAYER 1: Deterministic - Parse libraryfolders.vdf

        private static async Task<List<SteamLibraryFolder>> ParseLibraryFoldersAsync()
        {
            var folders = new List<SteamLibraryFolder>();

            if (!File.Exists(SteamLibraryFoldersPath))
            {
                Debug.WriteLine($"✗ libraryfolders.vdf not found at: {SteamLibraryFoldersPath}");
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
                                Debug.WriteLine($"  Library: {folderPath} ({appIds.Count} apps)");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"✗ Error parsing libraryfolders.vdf: {ex.Message}");
            }

            return folders;
        }

        #endregion

        #region LAYER 2: Deterministic - Parse appmanifest_*.acf

        private static async Task<List<SteamGameInfo>> ParseAppManifestsAsync(string libraryPath, List<int> expectedAppIds)
        {
            var games = new List<SteamGameInfo>();
            string steamAppsPath = Path.Combine(libraryPath, "steamapps");

            if (!Directory.Exists(steamAppsPath))
            {
                Debug.WriteLine($"✗ steamapps not found: {steamAppsPath}");
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
                        Debug.WriteLine($"✗ Error parsing {Path.GetFileName(manifestFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"✗ Error reading manifests: {ex.Message}");
            }

            return games;
        }

        #endregion

        #region LAYER 3: Heuristic Executable Detection Funnel

        /// <summary>
        /// The Detection Funnel: A step-by-step algorithm prioritizing reliability.
        /// </summary>
        private static string ExecuteDetectionFunnel(string gameFolderPath, string gameName)
        {
            if (!Directory.Exists(gameFolderPath))
            {
                Debug.WriteLine($"  ✗ Folder does not exist");
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
                Debug.WriteLine($"  ✗ No valid candidates");
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
            Debug.WriteLine($"  ✓ SELECTED: {Path.GetFileName(finalCandidate.Path)} (Score: {finalCandidate.Score})");

            // Step 5: Return (caching happens in caller)
            return finalCandidate.Path;
        }

        #endregion

        #region Heuristic: Initial Exclusion (Negative Heuristic)

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

            // No file size check - all .exe files are valid candidates
            return false;
        }

        #endregion

        #region Heuristic: Weighted Scoring Model

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

            // CRITICAL PRIORITY: Launcher in Root Directory (+60): Almost always the intended entry point
            // Handles: Config, updates, anti-cheat initialization, language selection
            if (fileNameLower.Contains("launcher") && isRootDirectory)
            {
                score += 60;
                Debug.WriteLine($"      CRITICAL: Launcher in root directory (+60)");
            }

            // Title Match (+50): Fuzzy matching with Levenshtein distance
            double similarity = CalculateSimilarity(fileNameLower, gameNameLower);
            if (similarity > 0.8) score += 50;
            else if (similarity > 0.6) score += 30;
            else if (similarity > 0.4) score += 15;

            // Engine Build Suffix (+40): -shipping.exe (Unreal Engine)
            if (fileNameLower.EndsWith("-shipping"))
                score += 40;

            // "Launcher" Keyword (+35): Often the correct entry point (but lower than root launcher)
            if (fileNameLower.Contains("launcher") && !isRootDirectory)
                score += 35;

            // Architecture Match (+30): x64/win64 on 64-bit system
            if (Environment.Is64BitOperatingSystem)
            {
                // Check both filename and path for architecture markers
                bool hasX64InName = fileNameLower.Contains("x64") || fileNameLower.Contains("win64") || fileNameLower.Contains("64bit");
                bool hasX64InPath = relativePath.Contains(@"\x64\") || relativePath.Contains(@"\x64vk\") || relativePath.Contains(@"win64");

                if (hasX64InName || hasX64InPath)
                    score += 30;
            }

            // "Game" Keyword (+20)
            if (fileNameLower.Contains("game"))
                score += 20;

            // Root Directory (+10) - basic bonus for root location
            if (isRootDirectory)
                score += 10;

            // Standard Engine Directory (+40): Binaries/Win64, Bin, x64
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

            // "Editor" Keyword (-100): Development tool
            if (fileNameLower.Contains("editor"))
                score -= 100;

            // Generic Engine Names (-20)
            var genericNames = new[] { "ue4game", "unityplayer", "unitycrashandler" };
            if (genericNames.Any(name => fileNameLower == name))
                score -= 20;

            // Render API in Path (-10): Deprioritize specialized versions (Hades case)
            // Examples: x64Vk, x64GL, Vulkan, OpenGL folders
            var renderApiMarkers = new[] { "vk", "vulkan", "gl", "opengl", "dx11", "dx12" };
            if (renderApiMarkers.Any(api => relativePath.Contains(api)))
                score -= 10;

            // Config/Update Launchers in Tools/Utils directories (-20): Maintenance tools, not game launchers
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

        #endregion

        #region Advanced Vector Analysis

        private static (string Path, int Score) ApplyAdvancedVectorAnalysis(List<(string Path, int Score)> topCandidates, string gameFolderPath)
        {
            // Convert anonymous type to named tuple for the Select
            var candidates = topCandidates.Select(c => (c.Path, c.Score)).ToList();

            // Check for launcher pattern
            var launcherCandidate = candidates.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c.Path).ToLowerInvariant().Contains("launcher"));

            // Check for anti-cheat presence
            bool hasAntiCheat = DetectAntiCheat(gameFolderPath);

            if (hasAntiCheat && launcherCandidate != default)
            {
                Debug.WriteLine($"    → Anti-cheat detected, prioritizing launcher");
                return launcherCandidate;
            }

            // Default: return top candidate
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

        #region Helper Methods

        private static async Task<int?> FetchRawgIdAsync(string gameName, IProgress<string>? progress)
        {
            try
            {
                return await GameNameService.FindRawgIdByNameAsync(gameName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  ✗ RAWG ID fetch failed: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}