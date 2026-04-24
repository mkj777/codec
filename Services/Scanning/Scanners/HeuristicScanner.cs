using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            "Program Files",
            "Program Files (x86)",
            "Games",
            "XboxGames",
            "Epic Games",
            "GOG Games",
            @"Ubisoft Game Launcher\games",
            "EA Games",
            "Riot Games",
        };

        private static readonly HashSet<string> DirectoryBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            // OS-level folders that should never be game candidates
            "Users", "AppData", "ProgramData", "Windows", "System32", "SysWOW64",
            "$Recycle.Bin", "Recovery", "PerfLogs", "Config.Msi", "MSOCache",

            // System & Drivers
            "NVIDIA Corporation", "Intel", "AMD", "Realtek", "Common Files", "Drivers",
            "Windows Defender", "Windows NT", "WindowsPowerShell", "Microsoft",
            "Microsoft OneDrive", "Internet Explorer", "Windows Mail", "Windows Media Player",

            // Productivity Software
            "Adobe", "Autodesk", "LibreOffice", "7-Zip", "WinRAR", "Notepad++",
            "Microsoft Office", "VLC", "Zoom", "TeamViewer", "AnyDesk",

            // Development Tools
            "Microsoft Visual Studio", "Python39", "Python310", "Python311", "Git",
            "Docker", "nodejs", "Java", "JetBrains", "Android Studio", "Android", "Postman",

            // Browsers
            "Google Chrome", "Mozilla Firefox", "Microsoft Edge", "Opera", "Brave",

            // Game Launchers (not games themselves)
            "Steam", "Epic Games Launcher", "GOG Galaxy", "Ubisoft Connect",
            "EA App", "EA Games", "Battle.net", "Origin", "Xbox",
            "Riot Games", "Riot Client", "Riot Vanguard",

            // Emulators (not games themselves)
            "BlueStacks", "Nox", "LDPlayer", "MEmu", "Dolphin Emulator",
            "RPCS3", "Cemu", "Yuzu", "Ryujinx", "PCSX2", "ePSXe",

            // Typical dev folders
            "src", "lib", "docs", "test", "bin", "assets", "node_modules", "build",
            ".git", ".vs", "packages", "obj", "debug", "release"
        };

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            return Task.Run(() =>
            {
                var candidates = new List<GameCandidate>();

                foreach (var drive in GetReadyNonNetworkDrives())
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
                            string dirName = new DirectoryInfo(dir).Name;

                            if (DirectoryBlacklist.Contains(dirName) || NonGameSoftwareCatalog.IsNonGameDirectory(dirName, dir))
                            {
                                Debug.WriteLine($"  [BLACKLIST] Skip: {dirName}");
                                continue;
                            }

                            if (IsExcludedPath(dir))
                            {
                                Debug.WriteLine($"  [PLATFORM EXCLUDE] Skip: {dirName}");
                                continue;
                            }

                            TryAddCandidate(dir, dirName, candidates);
                        }
                    }
                }

                return candidates;
            });
        }

        private bool IsExcludedPath(string dir) =>
            _excludedPaths.Any(p =>
                dir.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        private void TryAddCandidate(string dir, string dirName, List<GameCandidate> candidates)
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

                if (!SafeEnumerateFiles(dir, "*.exe").Any())
                {
                    Debug.WriteLine($"  [STAGE 4 REJECT] No .exe files found: {dirName}");
                    return;
                }

                if (IsDocumentationOrMediaFolder(dir))
                {
                    Debug.WriteLine($"  [STAGE 5 REJECT] Documentation/Media folder: {dirName}");
                    return;
                }

                Debug.WriteLine($"  [PASSED] Candidate added: {dirName} ({dir})");
                candidates.Add(new GameCandidate(dirName, dir, "Heuristic Scan"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  [FUNNEL ERROR] Skipping '{dirName}' ({dir}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static IEnumerable<DriveInfo> GetReadyNonNetworkDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { yield break; }

            foreach (var drive in drives)
            {
                bool ready;
                try { ready = drive.IsReady; }
                catch { continue; }

                if (!ready || drive.DriveType == DriveType.Network)
                    continue;

                yield return drive;
            }
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

                foreach (var file in SafeEnumerateFiles(directory, "*.*"))
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
    }
}
