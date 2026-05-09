using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Phase 2: Heuristic environmental scanner.
    /// Checks well-known game install roots on every ready non-network drive.
    /// </summary>
    public class HeuristicScanner : PlatformScanner
    {
        public override string PlatformName => "Heuristic Scan";

        private IReadOnlyList<string> _excludedPaths = Array.Empty<string>();
        public void SetExcludedPaths(IReadOnlyList<string> paths) => _excludedPaths = paths;

        private static readonly string[] ScanRoots =
        {
            "",
            "Program Files",
            "Program Files (x86)",
            "Games",
            "Game",
            "Gaming",
            "XboxGames",
            "GOG Games",
            "EA Games",
            "Origin Games",
            "Epic Games",
            "Riot Games",
            "SteamLibrary\\steamapps\\common",
            "Steam\\steamapps\\common",
            "Program Files\\Steam\\steamapps\\common",
            "Program Files (x86)\\Steam\\steamapps\\common",
            "Program Files\\Epic Games",
            "Program Files (x86)\\Epic Games",
            "Program Files\\GOG Galaxy\\Games",
            "Program Files (x86)\\GOG Galaxy\\Games",
            "Program Files\\Ubisoft\\Ubisoft Game Launcher\\games",
            "Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\games",
            "Program Files\\EA Games",
            "Program Files (x86)\\EA Games",
            "Program Files\\Riot Games",
            "Program Files (x86)\\Riot Games",
            "Program Files\\Rockstar Games",
            "Program Files (x86)\\Rockstar Games",
            "Rockstar Games",
        };

        private static readonly HashSet<string> DirectoryBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            // OS-level folders that should never be game candidates
            "Users", "AppData", "ProgramData", "Windows", "System32", "SysWOW64",
            "$Recycle.Bin", "Recovery", "PerfLogs", "Config.Msi", "MSOCache",
            "Program Files", "Program Files (x86)",

            // System & Drivers
            "NVIDIA Corporation", "Intel", "AMD", "Realtek", "Common Files", "Drivers",
            "Windows Defender", "Windows NT", "WindowsPowerShell", "Microsoft",
            "Microsoft OneDrive", "Internet Explorer", "Windows Mail", "Windows Media Player",

            // Productivity Software
            "Adobe", "Autodesk", "LibreOffice", "7-Zip", "WinRAR", "Notepad++",
            "Microsoft Office", "VLC", "Zoom", "TeamViewer", "AnyDesk",

            // Launcher containers. Dedicated nested scan roots cover their game folders.
            "Steam", "SteamLibrary", "Epic Games", "Epic Games Launcher", "GOG Galaxy",
            "GOG Games", "Games", "Game", "Gaming", "XboxGames", "Ubisoft",
            "Ubisoft Game Launcher", "Ubisoft Connect", "Battle.net", "EA Desktop",
            "EA App", "EA Games", "Origin", "Origin Games", "Xbox", "Riot Games",
            "Riot Client", "Riot Vanguard", "Rockstar Games", "Electronic Arts",

            // Emulators (not games themselves)
            "BlueStacks", "Nox", "LDPlayer", "MEmu", "Dolphin Emulator",
            "RPCS3", "Cemu", "Yuzu", "Ryujinx", "PCSX2", "ePSXe",

            // Typical dev folders
            "src", "lib", "docs", "test", "bin", "assets", "node_modules", "build",
            "tools", "tool", "utilities", "utility",
            ".git", ".vs", "packages", "obj", "debug", "release"
        };

        private static readonly HashSet<string> ContainerDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Program Files", "Program Files (x86)",
            "Steam", "SteamLibrary", "Epic Games", "Epic Games Launcher", "GOG Galaxy",
            "GOG Games", "Games", "Game", "Gaming", "XboxGames", "Ubisoft",
            "Ubisoft Game Launcher", "Ubisoft Connect", "Battle.net", "EA Desktop",
            "EA App", "EA Games", "Origin", "Origin Games", "Xbox", "Riot Games",
            "Riot Client", "Rockstar Games", "Electronic Arts", "Library", "Libraries",
            "PC Games", "Installed Games"
        };

        private static readonly HashSet<string> UserGameContainerNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Games", "Game", "Gaming", "PC Games", "Installed Games",
            "GOG Games", "EA Games", "Origin Games", "XboxGames"
        };

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new ConcurrentBag<GameCandidate>();
            var directoriesToScan = await Task.Run(() => DiscoverCandidateDirectories(progress)).ConfigureAwait(false);

            int maxParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            var options = new ParallelOptions
            {
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = maxParallelism
            };

            await Parallel.ForEachAsync(directoriesToScan, options, (scanDir, _) =>
            {
                TryAddCandidate(scanDir.Path, scanDir.Name, candidates);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            return candidates
                .GroupBy(c => c.FolderPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private List<(string Path, string Name)> DiscoverCandidateDirectories(IProgress<string>? progress)
        {
            var directories = new List<(string Path, string Name)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var drive in LocalDriveDiscovery.GetReadyNonNetworkDrives())
            {
                string driveRoot = drive.RootDirectory.FullName;

                foreach (var relRoot in ScanRoots)
                {
                    string rootPath = Path.Combine(driveRoot, relRoot);
                    if (!Directory.Exists(rootPath) || IsExcludedPath(rootPath))
                        continue;

                    progress?.Report($"Scanning {rootPath}...");
                    Debug.WriteLine($"  [HEURISTIC] Scanning root: {rootPath}");

                    foreach (var dir in SafeGetDirectories(rootPath))
                    {
                        DiscoverCandidateDirectory(dir, relRoot, directories, seen);
                    }
                }
            }

            return directories;
        }

        private void DiscoverCandidateDirectory(
            string dir,
            string rootName,
            List<(string Path, string Name)> directories,
            HashSet<string> seen)
        {
            string dirName = new DirectoryInfo(dir).Name;

            if (IsExcludedPath(dir))
            {
                Debug.WriteLine($"  [PLATFORM EXCLUDE] Skip: {dirName}");
                return;
            }

            bool isContainer = ContainerDirectoryNames.Contains(dirName);
            bool isBlacklisted = DirectoryBlacklist.Contains(dirName) || NonGameSoftwareCatalog.IsNonGameDirectory(dirName, dir);

            if (!isBlacklisted && seen.Add(dir))
            {
                directories.Add((dir, dirName));
            }
            else if (isBlacklisted && !isContainer)
            {
                Debug.WriteLine($"  [BLACKLIST] Skip: {dirName}");
                return;
            }

            if (!ShouldScanOneLevelDeeper(rootName, dir, isContainer))
            {
                return;
            }

            foreach (var child in SafeGetDirectories(dir))
            {
                string childName = new DirectoryInfo(child).Name;
                if (IsExcludedPath(child))
                {
                    Debug.WriteLine($"  [PLATFORM EXCLUDE] Skip nested: {childName}");
                    continue;
                }

                if (DirectoryBlacklist.Contains(childName) || NonGameSoftwareCatalog.IsNonGameDirectory(childName, child))
                {
                    Debug.WriteLine($"  [BLACKLIST] Skip nested: {childName}");
                    continue;
                }

                if (seen.Add(child))
                {
                    directories.Add((child, childName));
                }
            }
        }

        private static bool ShouldScanOneLevelDeeper(string rootName, string dir, bool isContainer)
        {
            if (isContainer)
            {
                return true;
            }

            return !HasTopLevelExecutable(dir);
        }

        private static bool HasTopLevelExecutable(string dir)
        {
            try { return Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Length > 0; }
            catch { return false; }
        }

        private bool IsExcludedPath(string dir) =>
            _excludedPaths.Any(p =>
                dir.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        private void TryAddCandidate(string dir, string dirName, ConcurrentBag<GameCandidate> candidates)
        {
            try
            {
                if (IsExcludedPath(dir))
                {
                    Debug.WriteLine($"  [PLATFORM EXCLUDE] Owned by platform scanner: {dirName}");
                    return;
                }

                if (NonGameSoftwareCatalog.IsNonGameDirectory(dirName, dir))
                {
                    Debug.WriteLine($"  [CATALOG REJECT] Utility directory: {dirName}");
                    return;
                }

                if (DirectoryBlacklist.Contains(dirName))
                {
                    Debug.WriteLine($"  [STAGE 1 REJECT] Blacklisted directory: {dirName}");
                    return;
                }

                if (IsDeveloperProject(dir))
                {
                    Debug.WriteLine($"  [STAGE 2 REJECT] Developer project detected: {dirName}");
                    return;
                }

                if (IsEmulator(dir))
                {
                    Debug.WriteLine($"  [STAGE 3 REJECT] Emulator detected: {dirName}");
                    return;
                }

                if (!SafeEnumerateFilesWithinDepth(dir, "*.exe", maxDepth: 2, maxFiles: 1).Any())
                {
                    Debug.WriteLine($"  [STAGE 4 REJECT] No .exe files found: {dirName}");
                    return;
                }

                if (HasElectronOrCefSignals(dir))
                {
                    Debug.WriteLine($"  [STAGE 5 REJECT] Electron/CEF software fingerprint: {dirName}");
                    return;
                }

                GameFingerprint fingerprint = ComputeGameFingerprint(dir);
                if (!fingerprint.HasLocalGameEvidence)
                {
                    Debug.WriteLine($"  [STAGE 6 REJECT] Weak generic fingerprint ({fingerprint.DebugDetails}): {dirName}");
                    return;
                }

                int gameScore = fingerprint.Score;
                if (gameScore < -15)
                {
                    Debug.WriteLine($"  [STAGE 6 REJECT] Software pattern (score={gameScore}): {dirName}");
                    return;
                }

                if (IsDocumentationOrMediaFolder(dir))
                {
                    Debug.WriteLine($"  [STAGE 7 REJECT] Documentation/Media folder: {dirName}");
                    return;
                }

                bool hasGameSignals = fingerprint.HasStrongGameSignals;
                if (hasGameSignals)
                    Debug.WriteLine($"  [GAME SIGNALS] Strong game content (score={gameScore}): {dirName}");

                Debug.WriteLine($"  [PASSED] Candidate added: {dirName} (score={gameScore}, {dir})");
                candidates.Add(new GameCandidate(dirName, dir, "Heuristic Scan", HasStrongGameSignals: hasGameSignals));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  [FUNNEL ERROR] Skipping '{dirName}' ({dir}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool HasElectronOrCefSignals(string directory)
        {
            try
            {
                var rootFiles = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f).ToLowerInvariant())
                    .ToHashSet();

                if (rootFiles.Any(f => f.EndsWith(".asar"))) return true;
                if (rootFiles.Contains("libcef.dll")) return true;
                if (rootFiles.Contains("icudtl.dat")) return true;
                if (rootFiles.Contains("v8_context_snapshot.bin")) return true;
                if (rootFiles.Contains("snapshot_blob.bin")) return true;
                if (rootFiles.Contains("package.json")) return true;

                if (rootFiles.Contains("resources.pak"))
                {
                    var subDirNames = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                        .Select(d => new DirectoryInfo(d).Name.ToLowerInvariant());
                    if (subDirNames.Contains("locales")) return true;
                }
            }
            catch { }
            return false;
        }

        private static readonly HashSet<string> StrongEngineDlls = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam_api.dll", "steam_api64.dll",
            "eossdk-win64-shipping.dll",
            "goggameservices.dll", "galaxy.dll",
            "unityplayer.dll",
            "bink2w64.dll", "binkw32.dll",
            "physxdevice64.dll",
        };

        private static readonly HashSet<string> StrongGameAssetDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "textures", "texture", "shaders", "shader", "levels", "maps", "worlds",
            "audio", "sounds", "sound", "music", "meshes", "models", "animations",
            "effects", "particles", "cutscenes", "movies", "savegames"
        };

        private static readonly Dictionary<string, int> GameAssetDirScores = new(StringComparer.OrdinalIgnoreCase)
        {
            ["textures"] = 40,
            ["texture"] = 40,
            ["shaders"] = 30,
            ["shader"] = 30,
            ["levels"] = 40,
            ["maps"] = 35,
            ["worlds"] = 35,
            ["audio"] = 30,
            ["sounds"] = 30,
            ["sound"] = 30,
            ["music"] = 25,
            ["content"] = 35,
            ["assets"] = 20,
            ["meshes"] = 30,
            ["models"] = 25,
            ["animations"] = 20,
            ["save"] = 15,
            ["saves"] = 15,
            ["savegames"] = 20,
            ["effects"] = 25,
            ["particles"] = 25,
            ["cutscenes"] = 30,
            ["movies"] = 20,
            ["localization"] = 15,
        };

        private static int ComputeGameLikelihoodScore(string directory)
            => ComputeGameFingerprint(directory).Score;

        private sealed record GameFingerprint(
            int Score,
            bool HasLocalGameEvidence,
            bool HasStrongGameSignals,
            string DebugDetails);

        private sealed record ExecutableIdentityResult(
            int Score,
            bool HasExecutableNameMatch,
            bool HasMetadataNameMatch,
            bool HasKnownGamePublisher,
            bool HasUtilityMetadata);

        private static GameFingerprint ComputeGameFingerprint(string directory)
        {
            int score = 0;

            string[] rootFiles;
            try { rootFiles = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly); }
            catch { return new GameFingerprint(0, false, false, "rootFiles=unreadable"); }

            var dllFilesWithinInstall = SafeEnumerateFilesWithinDepth(directory, "*.dll", maxDepth: 2, maxFiles: 500).ToList();
            var exeFilesWithinInstall = SafeEnumerateFilesWithinDepth(directory, "*.exe", maxDepth: 2, maxFiles: 80)
                .Where(exe => !IsUtilityExecutableName(Path.GetFileNameWithoutExtension(exe)))
                .ToList();

            // Group root files by extension for bulk checks
            var byExt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in rootFiles)
            {
                string name = Path.GetFileName(f);
                string ext = Path.GetExtension(f);
                rootNames.Add(name);
                if (!byExt.TryGetValue(ext, out var list))
                    byExt[ext] = list = new List<string>();
                list.Add(f);
            }

            // Strong engine DLL → definite game
            bool hasStrongEngineDll = dllFilesWithinInstall.Select(Path.GetFileName).Any(n => n != null && StrongEngineDlls.Contains(n));
            if (hasStrongEngineDll)
                score += 100;

            bool hasSteamAppMarker = rootNames.Contains("steam_appid.txt")
                || rootNames.Any(name => name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".info", StringComparison.OrdinalIgnoreCase));
            if (hasSteamAppMarker)
                score += 80;

            // Unreal crash handler in root
            if (rootNames.Contains("crashreportclient.exe") || rootNames.Contains("unrealcrashandler64.exe"))
                score += 60;

            // DLL count
            int rootDllCount = byExt.TryGetValue(".dll", out var dlls) ? dlls.Count : 0;
            int dllCount = dllFilesWithinInstall.Count;
            int subfolderDllCount = Math.Max(0, dllCount - rootDllCount);
            score += dllCount switch
            {
                0 => -10,
                1 => -25,
                <= 3 => 5,
                <= 10 => 25,
                _ => 45,
            };

            // Game asset file types
            bool hasLargeGamePak = false;
            if (byExt.TryGetValue(".pak", out var paks))
            {
                foreach (var p in paks)
                {
                    string fileName = Path.GetFileName(p);
                    if (fileName.Equals("resources.pak", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        if (new FileInfo(p).Length > 1024 * 1024)
                        {
                            hasLargeGamePak = true;
                            score += 40;
                            break;
                        }
                    }
                    catch { }
                }
            }
            bool hasGameAudioBank = byExt.ContainsKey(".bank") || byExt.ContainsKey(".wem") || byExt.ContainsKey(".bnk");
            if (byExt.ContainsKey(".bank")) score += 50; // FMOD audio bank
            if (byExt.ContainsKey(".wem") || byExt.ContainsKey(".bnk")) score += 50; // Wwise audio
            bool hasTextureSet = byExt.TryGetValue(".dds", out var ddsFiles) && ddsFiles.Count >= 3;
            if (hasTextureSet) score += 35;

            int audioCount = (byExt.TryGetValue(".ogg", out var oggs) ? oggs.Count : 0)
                           + (byExt.TryGetValue(".wav", out var wavs) ? wavs.Count : 0);
            score += audioCount >= 5 ? 25 : audioCount >= 2 ? 10 : 0;

            if (byExt.ContainsKey(".sav")) score += 15;

            bool hasGameAssetFile = hasLargeGamePak || hasGameAudioBank || hasTextureSet || byExt.ContainsKey(".sav");

            foreach (var ext in new[] { ".dat", ".bin" })
            {
                if (byExt.TryGetValue(ext, out var binFiles))
                    foreach (var bf in binFiles)
                    {
                        try { if (new FileInfo(bf).Length > 50L * 1024 * 1024) { score += 25; break; } } catch { }
                    }
            }

            // Subdirectory analysis
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly); }
            catch { subDirs = Array.Empty<string>(); }

            int subExeCount = 0;
            bool hasStrongGameAssetDirectory = false;
            foreach (var sub in subDirs)
            {
                string subName = new DirectoryInfo(sub).Name;

                // Unity _Data pattern (e.g. "GameName_Data/")
                if (subName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    score += 60;
                    continue;
                }

                // Unreal Binaries directory
                if (subName.Equals("Binaries", StringComparison.OrdinalIgnoreCase))
                    score += 35;

                // Named game asset directories
                if (GameAssetDirScores.TryGetValue(subName, out int dirBonus))
                {
                    score += dirBonus;
                    hasStrongGameAssetDirectory |= StrongGameAssetDirectoryNames.Contains(subName);
                }

                // Count exes in immediate subdirs (service/daemon pattern)
                try { subExeCount += Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).Length; }
                catch { }
            }

            ExecutableIdentityResult executableIdentity = ComputeExecutableIdentity(directory, exeFilesWithinInstall);
            score += executableIdentity.Score;

            // Service/daemon penalty: multiple sub-exes with very few DLLs and no game-like executable identity.
            bool hasSubfolderRuntimeSupport = subfolderDllCount >= 4 || dllCount >= 8;
            bool hasExecutableGameIdentity = executableIdentity.Score >= 25;
            if (!hasSubfolderRuntimeSupport && !hasExecutableGameIdentity)
            {
                if (subExeCount >= 3) score -= 20;
                else if (subExeCount >= 2 && dllCount <= 2) score -= 15;
            }

            // No recognisable game asset types → software-like
            bool hasAnyGameAsset = byExt.ContainsKey(".pak") || byExt.ContainsKey(".bank")
                || byExt.ContainsKey(".wem") || byExt.ContainsKey(".bnk")
                || byExt.ContainsKey(".dds") || audioCount > 0 || byExt.ContainsKey(".sav");
            if (!hasAnyGameAsset && score < 60)
                score -= 20;

            bool hasGameContainerPath = IsUnderUserGameContainer(directory);
            bool hasLocalGameEvidence =
                hasStrongEngineDll ||
                hasSteamAppMarker ||
                hasGameAssetFile ||
                hasStrongGameAssetDirectory ||
                executableIdentity.HasKnownGamePublisher ||
                (hasGameContainerPath && (executableIdentity.HasExecutableNameMatch || executableIdentity.HasMetadataNameMatch));

            if (executableIdentity.HasUtilityMetadata && !hasLocalGameEvidence)
            {
                score -= 30;
            }

            bool hasStrongGameSignals =
                hasStrongEngineDll ||
                hasSteamAppMarker ||
                hasGameAssetFile ||
                (hasStrongGameAssetDirectory && score >= 40) ||
                (executableIdentity.HasKnownGamePublisher && score >= 35);

            string debugDetails = $"score={score}, rootDlls={rootDllCount}, subDlls={subfolderDllCount}, subExes={subExeCount}, exeIdentity={executableIdentity.Score}, gameEvidence={hasLocalGameEvidence}, strong={hasStrongGameSignals}";
            Debug.WriteLine($"  [SCORE] {new DirectoryInfo(directory).Name}: {debugDetails}");
            return new GameFingerprint(score, hasLocalGameEvidence, hasStrongGameSignals, debugDetails);
        }

        private static int ComputeExecutableIdentityScore(string directory, IReadOnlyList<string> executablePaths)
            => ComputeExecutableIdentity(directory, executablePaths).Score;

        private static ExecutableIdentityResult ComputeExecutableIdentity(string directory, IReadOnlyList<string> executablePaths)
        {
            if (executablePaths.Count == 0)
            {
                return new ExecutableIdentityResult(0, false, false, false, false);
            }

            string directoryName = new DirectoryInfo(directory).Name;
            string normalizedDirectory = NormalizeFingerprintText(directoryName);
            var directoryTokens = TokenizeFingerprintText(normalizedDirectory);

            var likelyExecutables = executablePaths
                .Select(path => new
                {
                    Path = path,
                    IsRoot = string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase),
                    Size = TryGetFileSize(path)
                })
                .OrderByDescending(exe => exe.IsRoot)
                .ThenByDescending(exe => exe.Size)
                .Take(8)
                .ToList();

            int bestScore = 0;
            bool bestHasExecutableNameMatch = false;
            bool bestHasMetadataNameMatch = false;
            bool hasKnownGamePublisher = false;
            bool hasUtilityMetadata = false;

            foreach (var executable in likelyExecutables)
            {
                int score = executable.IsRoot ? 10 : 0;
                string executableName = Path.GetFileNameWithoutExtension(executable.Path);
                string normalizedExe = NormalizeFingerprintText(executableName);
                int executableNameScore = ScoreTokenOverlap(directoryTokens, TokenizeFingerprintText(normalizedExe), exactBonus: 25);
                score += executableNameScore;
                bool executableNameMatches = executableNameScore > 0;
                bool metadataNameMatches = false;

                try
                {
                    var info = FileVersionInfo.GetVersionInfo(executable.Path);
                    string metadata = string.Join(' ', new[]
                    {
                        info.ProductName,
                        info.FileDescription,
                        info.InternalName,
                        info.OriginalFilename,
                        info.CompanyName
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));

                    string normalizedMetadata = NormalizeFingerprintText(metadata);
                    if (!string.IsNullOrWhiteSpace(normalizedMetadata) && !IsUtilityExecutableName(normalizedMetadata))
                    {
                        score += 10;
                        int metadataScore = ScoreTokenOverlap(directoryTokens, TokenizeFingerprintText(normalizedMetadata), exactBonus: 35);
                        score += metadataScore;
                        metadataNameMatches = metadataScore > 0;
                    }
                    else if (!string.IsNullOrWhiteSpace(normalizedMetadata))
                    {
                        hasUtilityMetadata = true;
                    }

                    if (LooksLikeGamePublisher(info.CompanyName))
                    {
                        score += 15;
                        hasKnownGamePublisher = true;
                    }
                }
                catch { }

                int boundedScore = Math.Min(score, 75);
                if (boundedScore > bestScore)
                {
                    bestScore = boundedScore;
                    bestHasExecutableNameMatch = executableNameMatches;
                    bestHasMetadataNameMatch = metadataNameMatches;
                }
            }

            return new ExecutableIdentityResult(
                bestScore,
                bestHasExecutableNameMatch,
                bestHasMetadataNameMatch,
                hasKnownGamePublisher,
                hasUtilityMetadata);
        }

        private static bool IsUnderUserGameContainer(string directory)
        {
            try
            {
                string? parentName = Directory.GetParent(directory)?.Name;
                return parentName is not null && UserGameContainerNames.Contains(parentName);
            }
            catch
            {
                return false;
            }
        }

        private static long TryGetFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static int ScoreTokenOverlap(IReadOnlySet<string> directoryTokens, IReadOnlySet<string> candidateTokens, int exactBonus)
        {
            if (directoryTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return 0;
            }

            int overlap = directoryTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
            if (overlap == 0)
            {
                return 0;
            }

            int score = overlap * 8;
            if (directoryTokens.All(candidateTokens.Contains))
            {
                score += exactBonus;
            }

            return Math.Min(score, exactBonus + 30);
        }

        private static string NormalizeFingerprintText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value
                .Replace("™", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("®", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("©", " ", StringComparison.OrdinalIgnoreCase);

            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"(?<=[a-z])(?=[A-Z])", " ");
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(tm|r)\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^A-Za-z0-9]+", " ");
            return normalized.ToLowerInvariant().Trim();
        }

        private static IReadOnlySet<string> TokenizeFingerprintText(string value)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "of", "for", "game", "launcher", "app", "application",
                "tm", "r", "x64", "x86", "win64", "win32", "exe"
            };

            return value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => token.Length >= 2 && !stopWords.Contains(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool LooksLikeGamePublisher(string? companyName)
        {
            if (string.IsNullOrWhiteSpace(companyName))
            {
                return false;
            }

            string normalized = NormalizeFingerprintText(companyName);
            string[] publishers =
            {
                "electronic arts", "ea", "maxis", "ubisoft", "valve", "capcom",
                "bethesda", "rockstar", "square enix", "sega", "bandai namco",
                "2k", "cd projekt", "paradox", "devolver", "riot games"
            };

            return publishers.Any(publisher => normalized.Contains(publisher, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUtilityExecutableName(string? executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return true;
            }

            string normalized = NormalizeFingerprintText(executableName);
            string[] utilityTerms =
            {
                "setup", "install", "installer", "uninstall", "cleanup", "touchup",
                "crash", "report", "redist", "vcredist", "directx", "dxsetup",
                "helper", "service", "daemon", "updater", "repair", "activation"
            };

            return utilityTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> SafeGetDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return Array.Empty<string>(); }
        }

        private static bool IsDeveloperProject(string directory)
        {
            try
            {
                var subDirs = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .Select(d => new DirectoryInfo(d).Name.ToLowerInvariant())
                    .ToHashSet();

                var devIndicators = new[] { "src", "lib", "test", "tests", "docs", ".git", ".vs", "node_modules", "packages" };
                return devIndicators.Count(indicator => subDirs.Contains(indicator)) >= 3;
            }
            catch { return false; }
        }

        private static bool IsEmulator(string directory)
        {
            try
            {
                var dirName = new DirectoryInfo(directory).Name.ToLowerInvariant();
                var emulatorNames = new[]
                {
                    "bluestacks", "nox", "ldplayer", "memu", "dolphin",
                    "rpcs3", "cemu", "yuzu", "ryujinx", "pcsx2", "epsxe",
                    "duckstation", "retroarch", "ppsspp"
                };

                if (emulatorNames.Any(emu => dirName.Contains(emu)))
                    return true;

                var fileNames = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f).ToLowerInvariant())
                    .ToArray();

                return fileNames.Any(f => f.Contains("com.bluestacks") ||
                                          f.Contains("noxplayer") ||
                                          f.Contains("androidemulator"));
            }
            catch { return false; }
        }

        private static bool IsDocumentationOrMediaFolder(string directory)
        {
            try
            {
                int totalFiles = 0, docFiles = 0, mediaFiles = 0, exeFiles = 0;

                foreach (var file in SafeEnumerateFiles(directory, "*.*").Take(750))
                {
                    totalFiles++;
                    switch (Path.GetExtension(file).ToLowerInvariant())
                    {
                        case ".pdf" or ".txt" or ".docx" or ".md": docFiles++; break;
                        case ".mp3" or ".mp4" or ".avi" or ".mkv" or ".jpg" or ".png": mediaFiles++; break;
                        case ".exe": exeFiles++; break;
                    }
                }

                if (totalFiles == 0) return false;
                return (double)(docFiles + mediaFiles) / totalFiles > 0.7 && exeFiles < 3;
            }
            catch { return false; }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string rootPath, string searchPattern)
        {
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                string current = stack.Pop();

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(current, searchPattern, SearchOption.TopDirectoryOnly); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException)
                { Debug.WriteLine($"  [ACCESS] Skipping files in '{current}': {ex.Message}"); }

                foreach (var file in files) yield return file;

                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(current); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException)
                { Debug.WriteLine($"  [ACCESS] Skipping subdirectories of '{current}': {ex.Message}"); continue; }

                foreach (var subDir in subDirs) stack.Push(subDir);
            }
        }

        private static IEnumerable<string> SafeEnumerateFilesWithinDepth(
            string rootPath,
            string searchPattern,
            int maxDepth,
            int maxFiles)
        {
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((rootPath, 0));
            int yielded = 0;

            while (stack.Count > 0 && yielded < maxFiles)
            {
                var (current, depth) = stack.Pop();

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(current, searchPattern, SearchOption.TopDirectoryOnly); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException)
                { Debug.WriteLine($"  [ACCESS] Skipping files in '{current}': {ex.Message}"); }

                foreach (var file in files)
                {
                    yield return file;
                    yielded++;
                    if (yielded >= maxFiles)
                    {
                        yield break;
                    }
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(current); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException)
                { Debug.WriteLine($"  [ACCESS] Skipping subdirectories of '{current}': {ex.Message}"); continue; }

                foreach (var subDir in subDirs)
                {
                    stack.Push((subDir, depth + 1));
                }
            }
        }
    }
}
