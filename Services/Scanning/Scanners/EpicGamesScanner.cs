using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Epic Games Store launcher integration - JSON manifest parsing
    /// </summary>
    public class EpicGamesScanner : PlatformScanner
    {
        private const string EpicManifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

        public override string PlatformName => "Epic Games Store";

        public override async Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            if (!Directory.Exists(EpicManifestsPath))
            {
                Debug.WriteLine("? Epic Games manifests directory not found");
                return candidates;
            }

            try
            {
                var manifestFiles = Directory.GetFiles(EpicManifestsPath, "*.item");

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        string jsonContent = await File.ReadAllTextAsync(manifestFile);
                        using var doc = JsonDocument.Parse(jsonContent);

                        if (doc.RootElement.TryGetProperty("InstallLocation", out var location) &&
                            doc.RootElement.TryGetProperty("DisplayName", out var displayName))
                        {
                            string installPath = location.GetString() ?? "";
                            string name = displayName.GetString() ?? "";

                            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                            {
                                candidates.Add(new GameCandidate(name, installPath, "Epic Games Store"));
                            }
                        }
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
    }
}
