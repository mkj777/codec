using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Steam launcher integration - deterministic VDF parsing
    /// </summary>
    public class SteamScanner : PlatformScanner
    {
        private const string SteamLibraryFoldersPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
        private record SteamLibraryFolder(string Path, List<int> AppIds);
        private record SteamGameInfo(int AppId, string Name, string InstallDir, string LibraryPath);
        private static readonly string[] LibraryFilePatterns = { "libraryfolders.vdf", "libraryfolder.vdf" };
        private static readonly HashSet<string> DirectorySkipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows",
            "$Recycle.Bin",
            "System Volume Information",
            "Recovery",
            "Config.Msi",
            "MSOCache",
            "PerfLogs"
        };

        public override string PlatformName => "Steam";

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            var libraryFolders = await ParseLibraryFoldersAsync();
            if (!libraryFolders.Any())
            {
                Debug.WriteLine("? No Steam library folders found");
                return candidates;
            }

            Debug.WriteLine($"? Found {libraryFolders.Count} Steam library folders");

            var installedGames = new List<SteamGameInfo>();
            foreach (var folder in libraryFolders)
            {
                var gamesInFolder = await ParseAppManifestsAsync(folder.Path, folder.AppIds);
                installedGames.AddRange(gamesInFolder);
            }

            Debug.WriteLine($"? Total installed Steam games: {installedGames.Count}");

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
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var definitionFiles = await Task.Run(DiscoverLibraryDefinitionFiles);
            if (definitionFiles.Count == 0)
            {
                return folders;
            }

            foreach (var definitionFile in definitionFiles)
            {
                await ParseLibraryDefinitionAsync(definitionFile, folders, processedFiles);
            }

            return folders
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SteamLibraryFolder(
                    group.Key,
                    group.SelectMany(f => f.AppIds).Distinct().ToList()))
                .ToList();
        }

        private async Task ParseLibraryDefinitionAsync(string filePath, List<SteamLibraryFolder> accumulator, HashSet<string> processedFiles)
        {
            if (!processedFiles.Add(filePath))
            {
                return;
            }

            try
            {
                string content = await File.ReadAllTextAsync(filePath);
                VProperty vdfData = VdfConvert.Deserialize(content);
                string rootKey = vdfData.Key ?? string.Empty;

                if (rootKey.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase) && vdfData.Value is VObject foldersObj)
                {
                    foreach (var folder in foldersObj)
                    {
                        if (folder.Value is not VObject folderData)
                            continue;

                        string? folderPath = ExtractFolderPath(folderData);
                        if (string.IsNullOrWhiteSpace(folderPath))
                            continue;

                        folderPath = NormalizePath(folderPath);
                        var appIds = ExtractAppIds(folderData);
                        accumulator.Add(new SteamLibraryFolder(folderPath, appIds));
                    }
                }
                else if (rootKey.Equals("libraryfolder", StringComparison.OrdinalIgnoreCase) && vdfData.Value is VObject pointerObj)
                {
                    string? launcherPath = ExtractLauncherPath(pointerObj);
                    string? steamRoot = GetSteamRootFromLauncher(launcherPath);
                    if (!string.IsNullOrEmpty(steamRoot))
                    {
                        string nested = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(nested))
                        {
                            await ParseLibraryDefinitionAsync(nested, accumulator, processedFiles);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error parsing {filePath}: {ex.Message}");
            }
        }

        private static List<string> DiscoverLibraryDefinitionFiles()
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(SteamLibraryFoldersPath))
            {
                files.Add(SteamLibraryFoldersPath);
            }

            foreach (var candidate in GetSeedLibraryPaths())
            {
                if (File.Exists(candidate))
                {
                    files.Add(candidate);
                }
            }

            foreach (var drive in GetReadyDrives())
            {
                foreach (var file in SafeEnumerateLibraryFiles(drive.RootDirectory.FullName))
                {
                    files.Add(file);
                }
            }

            return files.ToList();
        }

        private static IEnumerable<string> GetSeedLibraryPaths()
        {
            var seeds = new List<string>();
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                seeds.Add(Path.Combine(programFiles, "Steam", "steamapps", "libraryfolders.vdf"));
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                seeds.Add(Path.Combine(programFilesX86, "Steam", "steamapps", "libraryfolders.vdf"));
            }

            return seeds;
        }

        private static IEnumerable<DriveInfo> GetReadyDrives()
        {
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                yield break;
            }

            foreach (var drive in drives)
            {
                bool isReady;
                try
                {
                    isReady = drive.IsReady;
                }
                catch
                {
                    continue;
                }

                if (!isReady)
                    continue;

                yield return drive;
            }
        }

        private static IEnumerable<string> SafeEnumerateLibraryFiles(string rootPath)
        {
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                string current = stack.Pop();

                foreach (var file in GetFilesSafe(current))
                {
                    yield return file;
                }

                foreach (var subDir in GetDirectoriesSafe(current))
                {
                    if (ShouldSkipDirectory(subDir))
                        continue;

                    stack.Push(subDir);
                }
            }
        }

        private static IEnumerable<string> GetFilesSafe(string directory)
        {
            foreach (var pattern in LibraryFilePatterns)
            {
                string[] files = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex))
                {
                    Debug.WriteLine($"? [SteamScanner] Skipping files in '{directory}': {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    yield return file;
                }
            }
        }

        private static IEnumerable<string> GetDirectoriesSafe(string directory)
        {
            string[] subDirs = Array.Empty<string>();
            try
            {
                subDirs = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                Debug.WriteLine($"? [SteamScanner] Skipping subdirectories of '{directory}': {ex.Message}");
            }

            foreach (var subDir in subDirs)
            {
                yield return subDir;
            }
        }

        private static bool ShouldSkipDirectory(string path)
        {
            try
            {
                var dirInfo = new DirectoryInfo(path);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || dirInfo.Attributes.HasFlag(FileAttributes.System))
                    return true;

                return DirectorySkipNames.Contains(dirInfo.Name);
            }
            catch
            {
                return true;
            }
        }

        private static bool IsExpectedFileSystemException(Exception ex) =>
            ex is UnauthorizedAccessException or PathTooLongException or IOException;

        private static string? ExtractFolderPath(VObject folderData)
        {
            foreach (var property in folderData)
            {
                if (property.Key.Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value?.ToString();
                }
            }

            return null;
        }

        private static List<int> ExtractAppIds(VObject folderData)
        {
            var ids = new List<int>();
            foreach (var property in folderData)
            {
                if (property.Key == "apps" && property.Value is VObject apps)
                {
                    foreach (var app in apps)
                    {
                        if (int.TryParse(app.Key, out int appId))
                        {
                            ids.Add(appId);
                        }
                    }
                }
            }

            return ids;
        }

        private static string? ExtractLauncherPath(VObject pointerObj)
        {
            foreach (var property in pointerObj)
            {
                if (property.Key.Equals("launcher", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value?.ToString();
                }
            }

            return null;
        }

        private static string NormalizePath(string rawPath)
        {
            string sanitized = rawPath.Replace('/', Path.DirectorySeparatorChar);
            try
            {
                return Path.GetFullPath(sanitized);
            }
            catch
            {
                return sanitized;
            }
        }

        private static string? GetSteamRootFromLauncher(string? launcherPath)
        {
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                return null;
            }

            string normalized = NormalizePath(launcherPath);
            try
            {
                if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(normalized);
                }

                return Directory.Exists(normalized) ? normalized : null;
            }
            catch
            {
                return null;
            }
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
}
