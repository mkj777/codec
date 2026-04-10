using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// GOG Galaxy launcher integration - Registry-based detection
    /// </summary>
    public class GOGScanner : PlatformScanner
    {
        public override string PlatformName => "GOG Galaxy";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");

                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        if (subKeyName.StartsWith("GOG.com", StringComparison.OrdinalIgnoreCase))
                        {
                            using var gameKey = key.OpenSubKey(subKeyName);
                            var installLocation = gameKey?.GetValue("InstallLocation") as string;
                            var displayName = gameKey?.GetValue("DisplayName") as string;

                            if (!string.IsNullOrEmpty(installLocation) &&
                                !string.IsNullOrEmpty(displayName) &&
                                Directory.Exists(installLocation))
                            {
                                candidates.Add(new GameCandidate(displayName, installLocation, "GOG Galaxy"));
                            }
                        }
                    }
                }

                string gogGamesPath = @"C:\GOG Games";
                if (Directory.Exists(gogGamesPath))
                {
                    foreach (var folder in Directory.GetDirectories(gogGamesPath))
                    {
                        string gameName = new DirectoryInfo(folder).Name;
                        if (!candidates.Any(c => c.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                        {
                            candidates.Add(new GameCandidate(gameName, folder, "GOG Galaxy"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning GOG Galaxy: {ex.Message}");
            }

            return Task.FromResult(candidates);
        }
    }
}
