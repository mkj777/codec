using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Codec.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Steam launcher integration — reads install path from registry, parses libraryfolders.vdf.
    /// VDF path is cached after first discovery; subsequent scans skip the lookup entirely.
    /// </summary>
    public class SteamScanner : PlatformScanner
    {
        private record SteamLibraryFolder(string Path, List<int> AppIds);
        private record SteamGameInfo(int AppId, string Name, string InstallDir, string LibraryPath);

        public override string PlatformName => "Steam";
        public string? DetectedSteamClientPath { get; private set; }

        private string? _cachedVdfPath;

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<GameCandidate>();

            var libraryFolders = await ParseLibraryFoldersAsync();
            if (!libraryFolders.Any())
            {
                Debug.WriteLine("? No Steam library folders found");
                return candidates;
            }

            Debug.WriteLine($"? Found {libraryFolders.Count} Steam library folders");

            foreach (var folder in libraryFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _knownLibraryPaths.Add(folder.Path);
            }

            var installedGames = new List<SteamGameInfo>();
            foreach (var folder in libraryFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gamesInFolder = await ParseAppManifestsAsync(folder.Path, folder.AppIds);
                installedGames.AddRange(gamesInFolder);
            }

            Debug.WriteLine($"? Total installed Steam games: {installedGames.Count}");

            foreach (var game in installedGames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string gameFolderPath = System.IO.Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);
                if (!IsInstallFolderPopulated(gameFolderPath))
                {
                    Debug.WriteLine($"[SteamScanner] SKIP '{game.Name}' (appid={game.AppId}): install folder missing or empty at {gameFolderPath}");
                    continue;
                }
                string? metadataLookupName = GameNameCleaner.TryGetFriendPassBaseName(game.Name, out string baseName)
                    ? baseName
                    : null;
                candidates.Add(new GameCandidate(game.Name, gameFolderPath, PlatformName, game.AppId, MetadataLookupName: metadataLookupName));
            }

            return candidates;
        }

        /// <summary>
        /// Returns false when Steam left the install dir behind after uninstall — directory missing,
        /// or present but contains no files at any depth. Walks lazily; bails on the first file found.
        /// </summary>
        internal static bool IsInstallFolderPopulated(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return false;

            try
            {
                return Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamScanner] IsInstallFolderPopulated failed for {folderPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns the cached VDF path, or discovers it via registry on first call.
        /// HKCU\Software\Valve\Steam\SteamPath always contains the correct Steam install location.
        /// </summary>
        private string? DiscoverVdfPath()
        {
            if (_cachedVdfPath != null)
                return _cachedVdfPath;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? steamPath = key?.GetValue("SteamPath") as string;
                if (string.IsNullOrWhiteSpace(steamPath))
                {
                    Debug.WriteLine("[SteamScanner] SteamPath registry value not found");
                    return null;
                }

                string normalized = NormalizePath(steamPath);
                string vdfPath = System.IO.Path.Combine(normalized, "steamapps", "libraryfolders.vdf");

                if (!File.Exists(vdfPath))
                {
                    Debug.WriteLine($"[SteamScanner] libraryfolders.vdf not found at: {vdfPath}");
                    return null;
                }

                _cachedVdfPath = vdfPath;
                Debug.WriteLine($"[SteamScanner] VDF path cached: {_cachedVdfPath}");
                return _cachedVdfPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamScanner] Registry lookup failed: {ex.Message}");
                return null;
            }
        }

        private async Task<List<SteamLibraryFolder>> ParseLibraryFoldersAsync()
        {
            var folders = new List<SteamLibraryFolder>();

            string? vdfPath = DiscoverVdfPath();
            if (vdfPath == null)
                return folders;

            // Set steam client path from vdf location: {steamapps}/libraryfolders.vdf -> {steam root}/steam.exe
            string? steamDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(vdfPath));
            if (!string.IsNullOrEmpty(steamDir))
            {
                string exe = System.IO.Path.Combine(steamDir, "steam.exe");
                if (File.Exists(exe))
                    DetectedSteamClientPath = exe;
            }

            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await ParseLibraryDefinitionAsync(vdfPath, folders, processedFiles);

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
                return;

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
                        string nested = System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(nested))
                            await ParseLibraryDefinitionAsync(nested, accumulator, processedFiles);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error parsing {filePath}: {ex.Message}");
            }
        }

        private static string? ExtractFolderPath(VObject folderData)
        {
            foreach (var property in folderData)
            {
                if (property.Key.Equals("path", StringComparison.OrdinalIgnoreCase))
                    return property.Value?.ToString();
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
                            ids.Add(appId);
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
                    return property.Value?.ToString();
            }

            return null;
        }

        private static string NormalizePath(string rawPath)
        {
            string sanitized = rawPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
            try
            {
                return System.IO.Path.GetFullPath(sanitized);
            }
            catch
            {
                return sanitized;
            }
        }

        private static string? GetSteamRootFromLauncher(string? launcherPath)
        {
            if (string.IsNullOrWhiteSpace(launcherPath))
                return null;

            string normalized = NormalizePath(launcherPath);
            try
            {
                if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return System.IO.Path.GetDirectoryName(normalized);

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
            string steamAppsPath = System.IO.Path.Combine(libraryPath, "steamapps");

            if (!Directory.Exists(steamAppsPath))
                return games;

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
                                games.Add(new SteamGameInfo(appId.Value, name, installDir, libraryPath));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"? Error parsing {System.IO.Path.GetFileName(manifestFile)}: {ex.Message}");
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
