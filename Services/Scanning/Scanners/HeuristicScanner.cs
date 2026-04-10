using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
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

            // Emulators (not games themselves)
            "BlueStacks", "Nox", "LDPlayer", "MEmu", "Dolphin Emulator",
            "RPCS3", "Cemu", "Yuzu", "Ryujinx", "PCSX2", "ePSXe",

            // Typical dev folders
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

                        if (NonGameSoftwareCatalog.IsNonGameDirectory(dirName, dir))
                        {
                            Debug.WriteLine($"  [CATALOG REJECT] Utility directory: {dirName}");
                            continue;
                        }

                        if (DirectoryBlacklist.Contains(dirName))
                        {
                            Debug.WriteLine($"  [STAGE 1 REJECT] Blacklisted directory: {dirName}");
                            continue;
                        }

                        if (IsDeveloperProject(dir))
                        {
                            Debug.WriteLine($"  [STAGE 2 REJECT] Developer project detected: {dirName}");
                            continue;
                        }

                        if (IsEmulator(dir))
                        {
                            Debug.WriteLine($"  [STAGE 3 REJECT] Emulator detected: {dirName}");
                            continue;
                        }

                        bool hasExecutable = SafeEnumerateFiles(dir, "*.exe").Any();
                        if (!hasExecutable)
                        {
                            Debug.WriteLine($"  [STAGE 4 REJECT] No .exe files found: {dirName}");
                            continue;
                        }

                        if (IsDocumentationOrMediaFolder(dir))
                        {
                            Debug.WriteLine($"  [STAGE 5 REJECT] Documentation/Media folder: {dirName}");
                            continue;
                        }

                        Debug.WriteLine($"  [PASSED] Candidate added: {dirName}");
                        candidates.Add(new GameCandidate(dirName, dir, "Heuristic Scan"));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"? Error scanning {targetDir}: {ex.Message}");
                }
            }

            return await Task.FromResult(candidates);
        }

        private static bool IsDeveloperProject(string directory)
        {
            try
            {
                var subDirs = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .Select(d => new DirectoryInfo(d).Name.ToLowerInvariant())
                    .ToHashSet();

                var devIndicators = new[] { "src", "lib", "test", "tests", "docs", ".git", ".vs", "node_modules", "packages" };
                int matchCount = devIndicators.Count(indicator => subDirs.Contains(indicator));
                return matchCount >= 3;
            }
            catch
            {
                return false;
            }
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

                var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
                var fileNames = files.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToArray();

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

        private static bool IsDocumentationOrMediaFolder(string directory)
        {
            try
            {
                int totalFiles = 0;
                int docFiles = 0;
                int mediaFiles = 0;
                int exeFiles = 0;

                foreach (var file in SafeEnumerateFiles(directory, "*.*"))
                {
                    totalFiles++;
                    var ext = Path.GetExtension(file).ToLowerInvariant();

                    switch (ext)
                    {
                        case ".pdf" or ".txt" or ".docx" or ".md":
                            docFiles++;
                            break;
                        case ".mp3" or ".mp4" or ".avi" or ".mkv" or ".jpg" or ".png":
                            mediaFiles++;
                            break;
                        case ".exe":
                            exeFiles++;
                            break;
                    }
                }

                if (totalFiles == 0)
                    return false;

                double nonGameRatio = (double)(docFiles + mediaFiles) / totalFiles;
                return nonGameRatio > 0.7 && exeFiles < 3;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string rootPath, string searchPattern)
        {
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                string current = stack.Pop();

                string[] files = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(current, searchPattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex))
                {
                    Debug.WriteLine($"  [ACCESS] Skipping files in '{current}': {ex.Message}");
                }

                foreach (var file in files)
                {
                    yield return file;
                }

                string[] subDirs = Array.Empty<string>();
                try
                {
                    subDirs = Directory.GetDirectories(current);
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex))
                {
                    Debug.WriteLine($"  [ACCESS] Skipping subdirectories of '{current}': {ex.Message}");
                    continue;
                }

                foreach (var subDir in subDirs)
                {
                    stack.Push(subDir);
                }
            }
        }

        private static bool IsExpectedFileSystemException(Exception ex) =>
            ex is UnauthorizedAccessException or PathTooLongException or IOException;
    }
}
