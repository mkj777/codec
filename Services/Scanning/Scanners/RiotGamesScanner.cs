using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    public class RiotGamesScanner : PlatformScanner
    {
        private static readonly string RiotGamesRoot = @"C:\Riot Games";
        private static readonly string StartMenuPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games";

        public override string PlatformName => "Riot Games";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            if (!Directory.Exists(RiotGamesRoot))
                return Task.FromResult(candidates);

            // Map folder names in C:\Riot Games\ (exclude Riot Client infrastructure folders)
            var gameFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(RiotGamesRoot))
            {
                var folderName = Path.GetFileName(dir);
                if (!folderName.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase))
                    gameFolders[folderName] = dir;
            }

            if (!Directory.Exists(StartMenuPath))
            {
                // No shortcuts: fall back to folder enumeration with exe detection
                foreach (var (name, path) in gameFolders)
                    candidates.Add(new GameCandidate(name, path, PlatformName));
                return Task.FromResult(candidates);
            }

            // Use Start Menu shortcuts as launch commands
            foreach (var lnk in Directory.GetFiles(StartMenuPath, "*.lnk"))
            {
                var gameName = Path.GetFileNameWithoutExtension(lnk);

                // Skip Riot launcher/infrastructure shortcuts (not games)
                if (gameName.Equals("Riot Games", StringComparison.OrdinalIgnoreCase)
                    || gameName.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase)
                    || gameName.Equals("Riot Vanguard", StringComparison.OrdinalIgnoreCase)
                    || gameName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;

                var folderPath = gameFolders.TryGetValue(gameName, out var matched)
                    ? matched
                    : Path.Combine(RiotGamesRoot, gameName);

                candidates.Add(new GameCandidate(gameName, folderPath, PlatformName, LaunchScriptPath: lnk));
            }

            return Task.FromResult(candidates);
        }
    }
}
