using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    public class RiotGamesScanner : PlatformScanner
    {
        private const string RiotGamesFolderName = "Riot Games";
        private static readonly string StartMenuPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games";
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
        };

        private readonly Func<IEnumerable<string>> _riotRootProvider;
        private readonly string _startMenuPath;

        public override string PlatformName => "Riot Games";

        public RiotGamesScanner()
            : this(FindRiotGamesRoots, StartMenuPath)
        {
        }

        internal RiotGamesScanner(Func<IEnumerable<string>> riotRootProvider, string startMenuPath)
        {
            _riotRootProvider = riotRootProvider;
            _startMenuPath = startMenuPath;
        }

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            var riotRoots = _riotRootProvider()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (riotRoots.Count == 0)
                return Task.FromResult(candidates);

            var shortcuts = FindStartMenuShortcuts(_startMenuPath);

            foreach (var root in riotRoots)
            {
                _knownLibraryPaths.Add(root);

                foreach (var dir in SafeGetDirectories(root))
                {
                    var folderName = Path.GetFileName(dir);
                    if (ShouldIgnoreGameFolder(folderName))
                        continue;

                    string gameName = GetDisplayName(folderName);
                    string? launchScript = TryMatchShortcut(folderName, gameName, shortcuts);
                    candidates.Add(new GameCandidate(gameName, dir, PlatformName, LaunchScriptPath: launchScript));
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

        private static string[] SafeGetDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return Array.Empty<string>(); }
        }

        private static Dictionary<string, string> FindStartMenuShortcuts(string startMenuPath)
        {
            var shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(startMenuPath))
                return shortcuts;

            foreach (var lnk in SafeGetFiles(startMenuPath, "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (ShouldIgnoreShortcut(name))
                    continue;

                shortcuts[name] = lnk;
            }

            return shortcuts;
        }

        private static string? TryMatchShortcut(string folderName, string gameName, IReadOnlyDictionary<string, string> shortcuts)
        {
            if (shortcuts.TryGetValue(gameName, out var launchScript) || shortcuts.TryGetValue(folderName, out launchScript))
                return launchScript;

            return null;
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
    }
}
