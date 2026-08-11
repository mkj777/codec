using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    public class RiotGamesScanner : PlatformScanner
    {
        private const string RiotGamesFolderName = "Riot Games";
        private static readonly string CommonStartMenuPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games";
        private static readonly string DefaultRiotClientPath = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
        private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Riot Client",
            "Riot Vanguard",
        };

        private static readonly HashSet<string> IgnoredShortcutNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Riot Games",
            "Riot Client",
            "Riot Vanguard",
            "Uninstall",
        };

        private static readonly Dictionary<string, string> FolderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LoR"] = "Legends of Runeterra",
            ["Lion"] = "2XKO",
        };

        private static readonly Dictionary<string, RiotLaunchProfile> LaunchProfiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["League of Legends"] = new("League of Legends", "league_of_legends"),
            ["Legends of Runeterra"] = new("Legends of Runeterra", "bacon"),
            ["LoR"] = new("Legends of Runeterra", "bacon"),
            ["VALORANT"] = new("VALORANT", "valorant"),
            ["2XKO"] = new("2XKO", "lion"),
            ["Lion"] = new("2XKO", "lion"),
        };

        private readonly Func<IEnumerable<string>> _riotRootProvider;
        private readonly IReadOnlyList<string> _startMenuPaths;
        private readonly ShortcutCreator _createShortcut;

        public override string PlatformName => "Riot Games";

        public RiotGamesScanner()
            : this(FindRiotGamesRoots, GetDefaultStartMenuPaths())
        {
        }

        internal RiotGamesScanner(Func<IEnumerable<string>> riotRootProvider, string startMenuPath)
            : this(riotRootProvider, new[] { startMenuPath })
        {
        }

        internal RiotGamesScanner(Func<IEnumerable<string>> riotRootProvider, string startMenuPath, ShortcutCreator createShortcut)
            : this(riotRootProvider, new[] { startMenuPath }, createShortcut)
        {
        }

        private RiotGamesScanner(Func<IEnumerable<string>> riotRootProvider, IReadOnlyList<string> startMenuPaths)
            : this(riotRootProvider, startMenuPaths, TryCreateWindowsShortcut)
        {
        }

        private RiotGamesScanner(Func<IEnumerable<string>> riotRootProvider, IReadOnlyList<string> startMenuPaths, ShortcutCreator createShortcut)
        {
            _riotRootProvider = riotRootProvider;
            _startMenuPaths = startMenuPaths;
            _createShortcut = createShortcut;
        }

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<GameCandidate>();
            var seenGameFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenLaunchTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var riotRoots = _riotRootProvider()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeDirectoryPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (riotRoots.Count == 0)
                return Task.FromResult(candidates);

            var shortcuts = FindStartMenuShortcuts(_startMenuPaths);

            foreach (var root in riotRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _knownLibraryPaths.Add(root);

                foreach (var dir in SafeGetDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folderName = Path.GetFileName(dir);
                    if (ShouldIgnoreGameFolder(folderName))
                        continue;

                    string normalizedGameFolder = NormalizeDirectoryPath(dir);
                    if (!seenGameFolders.Add(normalizedGameFolder))
                        continue;

                    string gameName = GetDisplayName(folderName);
                    string? launchScript = TryMatchShortcut(folderName, gameName, shortcuts);
                    launchScript ??= TryCreateMissingShortcut(root, folderName, gameName, shortcuts);

                    if (!string.IsNullOrWhiteSpace(launchScript))
                    {
                        string normalizedLaunchTarget = NormalizeDirectoryPath(launchScript);
                        if (!seenLaunchTargets.Add(normalizedLaunchTarget))
                            continue;
                    }

                    candidates.Add(new GameCandidate(gameName, normalizedGameFolder, PlatformName, LaunchScriptPath: launchScript));
                }
            }

            return Task.FromResult(candidates);
        }

        private static List<string> FindRiotGamesRoots()
        {
            var roots = new List<string>();

            foreach (var drive in LocalDriveDiscovery.GetReadyNonNetworkDrives())
            {
                string candidate;
                try { candidate = Path.Combine(drive.RootDirectory.FullName, RiotGamesFolderName); }
                catch { continue; }

                if (Directory.Exists(candidate))
                    roots.Add(candidate);
            }

            return roots;
        }

        private static IReadOnlyList<string> GetDefaultStartMenuPaths()
        {
            var paths = new List<string> { CommonStartMenuPath };

            string userProgramsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrWhiteSpace(userProgramsPath))
            {
                paths.Add(Path.Combine(userProgramsPath, "Riot Games"));
            }

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string[] SafeGetDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return Array.Empty<string>(); }
        }

        private static Dictionary<string, string> FindStartMenuShortcuts(IEnumerable<string> startMenuPaths)
        {
            var shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var startMenuPath in startMenuPaths)
            {
                if (!Directory.Exists(startMenuPath))
                    continue;

                foreach (var lnk in SafeGetFiles(startMenuPath, "*.lnk"))
                {
                    string name = Path.GetFileNameWithoutExtension(lnk);
                    if (ShouldIgnoreShortcut(name))
                        continue;

                    shortcuts.TryAdd(name, lnk);
                }
            }

            return shortcuts;
        }

        private static string? TryMatchShortcut(string folderName, string gameName, IReadOnlyDictionary<string, string> shortcuts)
        {
            if (shortcuts.TryGetValue(gameName, out var launchScript) || shortcuts.TryGetValue(folderName, out launchScript))
                return launchScript;

            return null;
        }

        private string? TryCreateMissingShortcut(
            string riotRoot,
            string folderName,
            string gameName,
            Dictionary<string, string> shortcuts)
        {
            if (!TryGetLaunchProfile(folderName, gameName, out var profile))
                return null;

            string targetPath = ResolveRiotClientPath(riotRoot);
            if (string.IsNullOrWhiteSpace(targetPath))
                return null;

            string workingDirectory = Path.GetDirectoryName(targetPath) ?? riotRoot;
            string shortcutFileName = profile.ShortcutName + ".lnk";

            foreach (var startMenuPath in _startMenuPaths)
            {
                if (string.IsNullOrWhiteSpace(startMenuPath))
                    continue;

                string shortcutPath = Path.Combine(startMenuPath, shortcutFileName);
                if (!_createShortcut(shortcutPath, targetPath, profile.Arguments, workingDirectory))
                    continue;

                if (!File.Exists(shortcutPath))
                    continue;

                shortcuts[profile.ShortcutName] = shortcutPath;
                return shortcutPath;
            }

            return null;
        }

        private static bool TryGetLaunchProfile(string folderName, string gameName, out RiotLaunchProfile profile)
        {
            if (LaunchProfiles.TryGetValue(gameName, out var gameProfile))
            {
                profile = gameProfile;
                return true;
            }

            if (LaunchProfiles.TryGetValue(folderName, out var folderProfile))
            {
                profile = folderProfile;
                return true;
            }

            profile = null!;
            return false;
        }

        private static string ResolveRiotClientPath(string riotRoot)
        {
            string rootClientPath = Path.Combine(riotRoot, "Riot Client", "RiotClientServices.exe");
            if (File.Exists(rootClientPath))
                return rootClientPath;

            return File.Exists(DefaultRiotClientPath)
                ? DefaultRiotClientPath
                : string.Empty;
        }

        private static bool TryCreateWindowsShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory)
        {
            object? shell = null;
            object? shortcut = null;

            try
            {
                string? shortcutDirectory = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrWhiteSpace(shortcutDirectory))
                {
                    Directory.CreateDirectory(shortcutDirectory);
                }

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return false;

                shell = Activator.CreateInstance(shellType);
                if (shell == null)
                    return false;

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: new object[] { shortcutPath });
                if (shortcut == null)
                    return false;

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());

                return File.Exists(shortcutPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        private static string GetDisplayName(string folderName) =>
            FolderDisplayNames.TryGetValue(folderName, out var displayName)
                ? displayName
                : folderName;

        private static bool ShouldIgnoreGameFolder(string folderName) =>
            IgnoredFolderNames.Contains(folderName)
            || folderName.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase);

        private static bool ShouldIgnoreShortcut(string shortcutName) =>
            IgnoredShortcutNames.Contains(shortcutName)
            || shortcutName.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase);

        private static string[] SafeGetFiles(string path, string searchPattern)
        {
            try { return Directory.GetFiles(path, searchPattern); }
            catch { return Array.Empty<string>(); }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        internal delegate bool ShortcutCreator(string shortcutPath, string targetPath, string arguments, string workingDirectory);

        private sealed record RiotLaunchProfile(string ShortcutName, string Product, string Patchline = "live")
        {
            public string Arguments => $"--launch-product={Product} --launch-patchline={Patchline}";
        }
    }
}
