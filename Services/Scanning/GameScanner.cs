using Codec.Services.Resolving;
using Codec.Services.Scanning.Scanners;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.Services.Scanning
{
    /// <summary>
    /// Multi-layered game scanner implementing the 3-phase "Confidence Funnel":
    /// Phase 1: High-reliability launcher integration
    /// Phase 2: Heuristic environmental scanning
    /// Phase 3: External validation and metadata enrichment
    /// </summary>
    public class GameScanner
    {
        private readonly List<PlatformScanner> _platformScanners;
        private readonly HeuristicScanner _heuristicScanner;
        private readonly GameNameService _gameName;

        public GameScanner(GameNameService gameName)
        {
            _gameName = gameName;
            _platformScanners = new List<PlatformScanner>
            {
                new SteamScanner(),
                new EpicGamesScanner(),
                new UbisoftConnectScanner(),
                new BattleNetScanner(),
                new EAAppScanner(),
                new GOGScanner()
            };
            _heuristicScanner = new HeuristicScanner();
        }

        /// <summary>
        /// Execute complete 3-phase scan
        /// </summary>
        public async Task<List<(int? SteamAppId, string GameName, int? RawgId, string ImportSource, string ExecutablePath, string FolderLocation)>> ScanAllGamesAsync(IProgress<string>? progress = null)
        {
            var allCandidates = new List<GameCandidate>();
            var scanCache = await ScanCache.LoadAsync();

            Debug.WriteLine("=== STARTING COMPLETE GAME LIBRARY SCAN ===");
            progress?.Report("Starting comprehensive game scan...");

            // PHASE 1: High-Reliability Launcher Integration
            Debug.WriteLine("\n=== PHASE 1: LAUNCHER INTEGRATION ===");
            foreach (var scanner in _platformScanners)
            {
                try
                {
                    progress?.Report($"Scanning {scanner.PlatformName}...");
                    var candidates = await scanner.ScanAsync(progress);
                    allCandidates.AddRange(candidates);
                    Debug.WriteLine($"? {scanner.PlatformName}: Found {candidates.Count} games");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"? {scanner.PlatformName} scan failed: {ex.Message}");
                    progress?.Report($"Warning: {scanner.PlatformName} scan failed");
                }
            }

            // PHASE 2: Heuristic Environmental Scanning
            Debug.WriteLine("\n=== PHASE 2: HEURISTIC SCANNING ===");
            try
            {
                progress?.Report("Scanning standard installation directories...");
                var heuristicCandidates = await _heuristicScanner.ScanAsync(progress);
                allCandidates.AddRange(heuristicCandidates);
                Debug.WriteLine($"? Heuristic scan: Found {heuristicCandidates.Count} potential games");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Heuristic scan failed: {ex.Message}");
            }

            // Remove duplicates based on folder path
            allCandidates = allCandidates
                .GroupBy(c => c.FolderPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // Drop known non-game software early to avoid expensive heuristics
            int beforeCatalogFilter = allCandidates.Count;
            allCandidates = allCandidates
                .Where(candidate => !NonGameSoftwareCatalog.IsNonGameCandidate(candidate))
                .ToList();

            if (beforeCatalogFilter != allCandidates.Count)
            {
                Debug.WriteLine($"\n- Non-game exclusions: Filtered {beforeCatalogFilter - allCandidates.Count} utility entries");
            }

            Debug.WriteLine($"\n? Total unique candidates: {allCandidates.Count}");

            // PHASE 3: External Validation & Enrichment + EXE Detection
            Debug.WriteLine("\n=== PHASE 3: VALIDATION & EXE DETECTION ===");
            progress?.Report($"Validating and analyzing {allCandidates.Count} candidates...");

            var validatedGames = new List<(int?, string, int?, string, string, string)>();
            int processedCount = 0;

            foreach (var candidate in allCandidates)
            {
                processedCount++;
                progress?.Report($"Validating {processedCount}/{allCandidates.Count}: {candidate.Name}");

                if (GameContentHeuristics.ShouldIgnoreCandidate(candidate.Name, candidate.FolderPath, candidate.Source, candidate.SteamAppId.HasValue))
                {
                    Debug.WriteLine($"  ? SKIPPED: '{candidate.Name}' flagged as utility/non-game");
                    continue;
                }

                if (scanCache.TryGetValid(candidate, out var cachedResult))
                {
                    Debug.WriteLine($"  ? Cache hit: '{candidate.Name}' (last scanned {cachedResult.CachedAtUtc:u})");
                    validatedGames.Add((cachedResult.SteamAppId, cachedResult.GameName, cachedResult.RawgId, cachedResult.ImportSource, cachedResult.ExecutablePath, cachedResult.FolderPath));
                    continue;
                }

                // Execute EXE detection funnel
                string executablePath = ExecutableDetector.ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name);

                if (string.IsNullOrEmpty(executablePath))
                {
                    Debug.WriteLine($"  ? REJECTED: '{candidate.Name}' - No valid executable found");
                    continue;
                }

                // Determine Steam ID: use existing if available, otherwise search for it
                int? steamId = candidate.SteamAppId;

                if (!steamId.HasValue)
                {
                    Debug.WriteLine($"  [STEAM LOOKUP] Searching Steam for: '{candidate.Name}'");
                    try
                    {
                        (int? foundSteamId, int? _) = await _gameName.FindGameIdsAsync(executablePath);
                        if (foundSteamId.HasValue)
                        {
                            steamId = foundSteamId;
                            Debug.WriteLine($"  ? Steam ID found: {steamId} for '{candidate.Name}'");
                        }
                        else
                        {
                            Debug.WriteLine($"  ? No Steam ID found for '{candidate.Name}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"  ? Steam lookup failed for '{candidate.Name}': {ex.Message}");
                    }
                }
                else
                {
                    Debug.WriteLine($"  ? Using existing Steam ID: {steamId} for '{candidate.Name}'");
                }

                // External validation via RAWG API
                int? rawgId = await ValidateAndFetchRawgIdAsync(candidate.Name, steamId.HasValue);

                // Apply strict validation: discard if not found in RAWG (unless from Phase 1)
                bool isFromLauncher = !candidate.Source.Equals("Heuristic Scan", StringComparison.OrdinalIgnoreCase);
                if (!rawgId.HasValue && !isFromLauncher)
                {
                    Debug.WriteLine($"  ? REJECTED: '{candidate.Name}' - Not found in RAWG database (likely not a game)");
                    continue;
                }

                Debug.WriteLine($"  ? VALIDATED: '{candidate.Name}' (Steam ID: {steamId?.ToString() ?? "N/A"}, RAWG ID: {rawgId?.ToString() ?? "N/A"})");
                validatedGames.Add((steamId, candidate.Name, rawgId, candidate.Source, executablePath, candidate.FolderPath));

                scanCache.Upsert(candidate, candidate.Name, executablePath, steamId, rawgId);
            }

            Debug.WriteLine($"\n=== SCAN COMPLETE: {validatedGames.Count} validated games ===");
            progress?.Report($"Scan complete! Found {validatedGames.Count} valid games");

            await scanCache.SaveAsync();

            return validatedGames;
        }

        private async Task<int?> ValidateAndFetchRawgIdAsync(string gameName, bool hasSteamContext)
        {
            try
            {
                var mode = hasSteamContext ? RawgValidationMode.SteamBacked : RawgValidationMode.Strict;
                return await _gameName.FindRawgIdByNameAsync(gameName, mode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  ? RAWG validation failed: {ex.Message}");
                return null;
            }
        }
    }
}
