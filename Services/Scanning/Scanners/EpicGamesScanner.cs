using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Epic Games Store launcher integration - JSON manifest parsing.
    /// </summary>
    public class EpicGamesScanner : PlatformScanner
    {
        private const string DefaultEpicManifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        private const string EpicLauncherRegistryKey = @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher";
        private const string UninstallRegistryKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        private static readonly string[] LauncherInstallDirs =
        {
            @"C:\Program Files (x86)\Epic Games\Launcher",
            @"C:\Program Files\Epic Games\Launcher"
        };

        private readonly Func<string?> _manifestsPathProvider;
        private readonly Func<string?> _launcherPathProvider;
        private readonly IReadOnlyList<string> _launcherInstallDirs;

        public override string PlatformName => "Epic Games Store";
        public string? DetectedEpicLauncherPath { get; private set; }

        public EpicGamesScanner()
            : this(DiscoverManifestsPath, DiscoverEpicLauncherExecutablePath, LauncherInstallDirs)
        {
        }

        internal EpicGamesScanner(string manifestsPath)
            : this(() => manifestsPath, () => null, Array.Empty<string>())
        {
        }

        internal EpicGamesScanner(string manifestsPath, string? launcherPath)
            : this(() => manifestsPath, () => launcherPath, Array.Empty<string>())
        {
        }

        private EpicGamesScanner(
            Func<string?> manifestsPathProvider,
            Func<string?> launcherPathProvider,
            IEnumerable<string> launcherInstallDirs)
        {
            _manifestsPathProvider = manifestsPathProvider;
            _launcherPathProvider = launcherPathProvider;
            _launcherInstallDirs = launcherInstallDirs.ToArray();
        }

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            DetectedEpicLauncherPath = NormalizeExistingFile(_launcherPathProvider());
            AddKnownPath(Path.GetDirectoryName(DetectedEpicLauncherPath));

            foreach (var dir in _launcherInstallDirs)
            {
                if (Directory.Exists(dir))
                {
                    AddKnownPath(dir);
                }
            }

            string? manifestsPath = _manifestsPathProvider();
            if (string.IsNullOrWhiteSpace(manifestsPath) || !Directory.Exists(manifestsPath))
            {
                Debug.WriteLine("? Epic Games manifests directory not found");
                return candidates;
            }

            try
            {
                var manifestFiles = Directory.GetFiles(manifestsPath, "*.item");

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        EpicManifestInfo? manifest = await ReadManifestAsync(manifestFile).ConfigureAwait(false);
                        if (manifest == null || !ShouldImportManifest(manifest))
                        {
                            continue;
                        }

                        string installPath = NormalizePath(manifest.InstallLocation!);
                        string? executableHint = ResolveLaunchExecutable(installPath, manifest.LaunchExecutable);

                        candidates.Add(new GameCandidate(
                            manifest.DisplayName!.Trim(),
                            installPath,
                            PlatformName,
                            EpicAppId: manifest.AppName!.Trim(),
                            ExecutableHintPath: executableHint));
                        AddKnownPath(installPath);
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

        private static async Task<EpicManifestInfo?> ReadManifestAsync(string manifestFile)
        {
            string jsonContent = await File.ReadAllTextAsync(manifestFile).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            return new EpicManifestInfo(
                DisplayName: ReadString(root, "DisplayName"),
                InstallLocation: ReadString(root, "InstallLocation"),
                AppName: ReadString(root, "AppName"),
                LaunchExecutable: ReadString(root, "LaunchExecutable"),
                IsApplication: ReadBool(root, "bIsApplication"),
                IsManaged: ReadBool(root, "bIsManaged"),
                CatalogNamespace: ReadString(root, "CatalogNamespace"),
                CatalogItemId: ReadString(root, "CatalogItemId"));
        }

        private static bool ShouldImportManifest(EpicManifestInfo manifest)
        {
            if (!manifest.IsApplication)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.AppName) ||
                string.IsNullOrWhiteSpace(manifest.DisplayName) ||
                string.IsNullOrWhiteSpace(manifest.InstallLocation))
            {
                return false;
            }

            return IsInstallFolderPopulated(NormalizePath(manifest.InstallLocation));
        }

        /// <summary>
        /// Returns false when Epic left a manifest behind after uninstall: directory missing,
        /// or present but containing no files at any depth.
        /// </summary>
        private static bool IsInstallFolderPopulated(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            try
            {
                return Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EpicGamesScanner] IsInstallFolderPopulated failed for {folderPath}: {ex.Message}");
                return false;
            }
        }

        private static string? ResolveLaunchExecutable(string installPath, string? launchExecutable)
        {
            if (string.IsNullOrWhiteSpace(launchExecutable))
            {
                return null;
            }

            string candidate = launchExecutable.Trim().Trim('"');
            if (!Path.IsPathRooted(candidate))
            {
                candidate = Path.Combine(installPath, candidate);
            }

            candidate = NormalizePath(candidate);
            return File.Exists(candidate) ? candidate : null;
        }

        private static string? DiscoverManifestsPath()
        {
            string? appDataPath = TryReadRegistryValue(EpicLauncherRegistryKey, "AppDataPath");
            string? manifestPath = TryBuildManifestPath(appDataPath);
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                return manifestPath;
            }

            return DefaultEpicManifestsPath;
        }

        private static string? TryBuildManifestPath(string? appDataPath)
        {
            if (string.IsNullOrWhiteSpace(appDataPath))
            {
                return null;
            }

            string normalized = NormalizePath(appDataPath.Trim().Trim('"'));
            string directoryName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(directoryName, "Manifests", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : Path.Combine(normalized, "Manifests");
        }

        private static string? DiscoverEpicLauncherExecutablePath()
        {
            string? fromUninstall = DiscoverLauncherFromUninstallRegistry();
            if (!string.IsNullOrWhiteSpace(fromUninstall))
            {
                return fromUninstall;
            }

            foreach (string dir in LauncherInstallDirs)
            {
                string? launcher = TryFindLauncherUnderRoot(dir);
                if (!string.IsNullOrWhiteSpace(launcher))
                {
                    return launcher;
                }
            }

            return null;
        }

        private static string? DiscoverLauncherFromUninstallRegistry()
        {
            try
            {
                using var uninstall = Registry.LocalMachine.OpenSubKey(UninstallRegistryKey);
                if (uninstall == null)
                {
                    return null;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using var appKey = uninstall.OpenSubKey(subKeyName);
                    string? displayName = appKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains("Epic Games Launcher", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? displayIcon = NormalizeExecutablePath(appKey?.GetValue("DisplayIcon") as string);
                    if (!string.IsNullOrWhiteSpace(displayIcon) && File.Exists(displayIcon))
                    {
                        return displayIcon;
                    }

                    string? installLocation = appKey?.GetValue("InstallLocation") as string;
                    string? launcher = TryFindLauncherUnderRoot(installLocation);
                    if (!string.IsNullOrWhiteSpace(launcher))
                    {
                        return launcher;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EpicGamesScanner] Launcher uninstall registry lookup failed: {ex.Message}");
            }

            return null;
        }

        private static string? TryFindLauncherUnderRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            string normalizedRoot = NormalizePath(root.Trim().Trim('"'));
            var candidates = new[]
            {
                Path.Combine(normalizedRoot, "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
                Path.Combine(normalizedRoot, "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
                Path.Combine(normalizedRoot, "EpicGamesLauncher.exe")
            };

            foreach (string candidate in candidates)
            {
                string? existing = NormalizeExistingFile(candidate);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            return null;
        }

        private static string? NormalizeExecutablePath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            string candidate = rawPath.Trim().Trim('"');
            int exeIndex = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                candidate = candidate.Substring(0, exeIndex + 4);
            }

            return NormalizePath(candidate);
        }

        private static string? NormalizeExistingFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string normalized = NormalizePath(path);
            return File.Exists(normalized) ? normalized : null;
        }

        private static string? TryReadRegistryValue(string subKeyPath, string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
                return key?.GetValue(valueName) as string;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EpicGamesScanner] Registry lookup failed for {subKeyPath}\\{valueName}: {ex.Message}");
                return null;
            }
        }

        private void AddKnownPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            if (!_knownLibraryPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _knownLibraryPaths.Add(path);
            }
        }

        private static string? ReadString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ToString();
        }

        private static bool ReadBool(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(property.GetString(), out bool value) && value,
                _ => false
            };
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

        private sealed record EpicManifestInfo(
            string? DisplayName,
            string? InstallLocation,
            string? AppName,
            string? LaunchExecutable,
            bool IsApplication,
            bool IsManaged,
            string? CatalogNamespace,
            string? CatalogItemId);
    }
}
