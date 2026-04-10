using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Ubisoft Connect launcher integration - YAML parsing
    /// </summary>
    public class UbisoftConnectScanner : PlatformScanner
    {
        public override string PlatformName => "Ubisoft Connect";

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Ubisoft Game Launcher\settings.yml"
            );

            if (!File.Exists(settingsPath))
            {
                Debug.WriteLine("? Ubisoft Connect settings.yml not found");
                return candidates;
            }

            try
            {
                string content = await File.ReadAllTextAsync(settingsPath);
                var lines = content.Split('\n');

                foreach (var line in lines)
                {
                    if (line.Contains("game_installation_path:"))
                    {
                        string path = line.Split(':')[1].Trim().Trim('"');
                        if (Directory.Exists(path))
                        {
                            var gameFolders = Directory.GetDirectories(path);
                            foreach (var folder in gameFolders)
                            {
                                string gameName = new DirectoryInfo(folder).Name;
                                candidates.Add(new GameCandidate(gameName, folder, "Ubisoft Connect"));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning Ubisoft Connect: {ex.Message}");
            }

            return candidates;
        }
    }
}
