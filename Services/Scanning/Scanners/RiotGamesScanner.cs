using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    public class RiotGamesScanner : PlatformScanner
    {
        private const string RiotGamesFolderName = "Riot Games";
        private static readonly string StartMenuPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games";

        public override string PlatformName => "Riot Games";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            var riotRoots = FindRiotGamesRoots();
            if (riotRoots.Count == 0)
                return Task.FromResult(candidates);

            // Aggregate game folders across all discovered Riot Games roots.
            var gameFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in riotRoots)
            {
                _knownLibraryPaths.Add(root);

                foreach (var dir in SafeGetDirectories(root))
                {
                    var folderName = Path.GetFileName(dir);
                    if (folderName.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase))
                        continue;
                    gameFolders[folderName] = dir;
                }
            }

            if (!Directory.Exists(StartMenuPath))
            {
                foreach (var (name, path) in gameFolders)
                    candidates.Add(new GameCandidate(name, path, PlatformName));
                return Task.FromResult(candidates);
            }

            foreach (var lnk in Directory.GetFiles(StartMenuPath, "*.lnk"))
            {
                var gameName = Path.GetFileNameWithoutExtension(lnk);

                if (gameName.Equals("Riot Games", StringComparison.OrdinalIgnoreCase)
                    || gameName.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase)
                    || gameName.Equals("Riot Vanguard", StringComparison.OrdinalIgnoreCase)
                    || gameName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;

                var folderPath = gameFolders.TryGetValue(gameName, out var matched)
                    ? matched
                    : Path.Combine(riotRoots[0], gameName);

                candidates.Add(new GameCandidate(gameName, folderPath, PlatformName, LaunchScriptPath: lnk));
            }

            return Task.FromResult(candidates);
        }

        private static List<string> FindRiotGamesRoots()
        {
            var roots = new List<string>();

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return roots; }

            foreach (var drive in drives)
            {
                bool ready;
                try { ready = drive.IsReady; }
                catch { continue; }

                if (!ready || drive.DriveType == DriveType.Network)
                    continue;

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
    }
}
