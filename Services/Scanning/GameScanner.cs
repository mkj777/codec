using Codec.Services.Resolving;
using Codec.Services.Scanning.Scanners;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Scanning
{
    /// <summary>
    /// Multi-layered game scanner implementing the 3-phase "Confidence Funnel":
    /// Phase 1: High-reliability launcher integration
    /// Phase 2: Heuristic environmental scanning
    /// Phase 3: External validation and metadata enrichment
    /// </summary>
    public sealed record ValidatedScanCandidate(
        int? SteamAppId,
        string GameName,
        int? RawgId,
        string ImportSource,
        string ExecutablePath,
        string FolderLocation,
        string? LaunchScriptPath = null);

    public class GameScanner
    {
        private readonly List<PlatformScanner> _platformScanners;
        private readonly HeuristicScanner _heuristicScanner;
        private readonly GameNameService _gameName;
        private readonly SteamScanner _steamScanner = new();

        public string? DetectedSteamClientPath => _steamScanner.DetectedSteamClientPath;

        public GameScanner(GameNameService gameName)
        {
            _gameName = gameName;
            _platformScanners = new List<PlatformScanner>
            {
                _steamScanner,
                new EpicGamesScanner(),
                new RiotGamesScanner()
            };
            _heuristicScanner = new HeuristicScanner();
        }

        /// <summary>
        /// Execute complete 3-phase scan
        /// </summary>
        public async IAsyncEnumerable<ValidatedScanCandidate> ScanIncrementallyAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var allCandidates = new List<GameCandidate>();
            var phase1Timings = new List<(string Name, long Ms, int Count)>();
            long phase2Ms = 0;
            int phase2Count = 0;
            long phase3Ms = 0;
            long cacheLoadMs;
            long cacheSaveMs;
            long dedupFilterMs;
            int cacheHits = 0;
            int newValidated = 0;
            int rejectedNoExe = 0;
            int rejectedNoRawg = 0;
            int skippedUtility = 0;
            int duplicateCount = 0;
            int catalogFiltered = 0;
            long steamLookupTotalMs = 0;
            int steamLookupCount = 0;
            long rawgValidationTotalMs = 0;
            int rawgValidationCount = 0;
            long exeDetectionTotalMs = 0;
            int exeDetectionCount = 0;

            var cacheSw = Stopwatch.StartNew();
            var scanCache = await ScanCache.LoadAsync();
            cacheSw.Stop();
            cacheLoadMs = cacheSw.ElapsedMilliseconds;

            Debug.WriteLine("=== STARTING COMPLETE GAME LIBRARY SCAN ===");
            progress?.Report("Starting comprehensive game scan...");

            // PHASE 1: High-Reliability Launcher Integration
            Debug.WriteLine("\n=== PHASE 1: LAUNCHER INTEGRATION ===");
            var phase1Sw = Stopwatch.StartNew();
            foreach (var scanner in _platformScanners)
            {
                var scannerSw = Stopwatch.StartNew();
                int count = 0;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"Scanning {scanner.PlatformName}...");
                    var candidates = await scanner.ScanAsync(progress);
                    count = candidates.Count;
                    allCandidates.AddRange(candidates);
                    scannerSw.Stop();
                    Debug.WriteLine($"  {scanner.PlatformName}: {count} games in {scannerSw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    scannerSw.Stop();
                    Debug.WriteLine($"  {scanner.PlatformName} FAILED in {scannerSw.ElapsedMilliseconds}ms: {ex.Message}");
                    progress?.Report($"Warning: {scanner.PlatformName} scan failed");
                }
                phase1Timings.Add((scanner.PlatformName, scannerSw.ElapsedMilliseconds, count));
            }
            phase1Sw.Stop();

            var allLibraryPaths = _platformScanners
                .SelectMany(s => s.KnownLibraryPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _heuristicScanner.SetExcludedPaths(allLibraryPaths);
            Debug.WriteLine($"  Platform library paths collected: {allLibraryPaths.Count}");

            // PHASE 2: Heuristic Environmental Scanning
            Debug.WriteLine("\n=== PHASE 2: HEURISTIC SCANNING ===");
            var phase2Sw = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Scanning standard installation directories...");
                var heuristicCandidates = await _heuristicScanner.ScanAsync(progress);
                phase2Count = heuristicCandidates.Count;
                allCandidates.AddRange(heuristicCandidates);
                Debug.WriteLine($"  Heuristic: {phase2Count} potential games");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  Heuristic FAILED: {ex.Message}");
            }
            phase2Sw.Stop();
            phase2Ms = phase2Sw.ElapsedMilliseconds;

            // Dedup + catalog filter
            var dedupSw = Stopwatch.StartNew();
            int beforeDedup = allCandidates.Count;
            allCandidates = allCandidates
                .GroupBy(c => c.FolderPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            duplicateCount = beforeDedup - allCandidates.Count;

            int beforeCatalogFilter = allCandidates.Count;
            allCandidates = allCandidates
                .Where(candidate => !NonGameSoftwareCatalog.IsNonGameCandidate(candidate))
                .ToList();
            catalogFiltered = beforeCatalogFilter - allCandidates.Count;
            dedupSw.Stop();
            dedupFilterMs = dedupSw.ElapsedMilliseconds;

            Debug.WriteLine($"\n  Dedup: removed {duplicateCount} duplicate folder paths");
            if (catalogFiltered > 0)
            {
                Debug.WriteLine($"  Catalog filter: removed {catalogFiltered} utility entries");
            }
            Debug.WriteLine($"  Unique candidates: {allCandidates.Count} ({dedupFilterMs}ms)");

            // PHASE 3: External Validation & Enrichment + EXE Detection
            Debug.WriteLine("\n=== PHASE 3: VALIDATION & EXE DETECTION ===");
            progress?.Report($"Validating and analyzing {allCandidates.Count} candidates...");
            var phase3Sw = Stopwatch.StartNew();

            int processedCount = 0;

            foreach (var candidate in allCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedCount++;
                progress?.Report($"Validating {processedCount}/{allCandidates.Count}: {candidate.Name}");

                if (GameContentHeuristics.ShouldIgnoreCandidate(candidate.Name, candidate.FolderPath, candidate.Source, candidate.SteamAppId.HasValue))
                {
                    Debug.WriteLine($"  SKIP '{candidate.Name}' (utility/non-game heuristic)");
                    skippedUtility++;
                    continue;
                }

                if (scanCache.TryGetValid(candidate, out var cachedResult))
                {
                    Debug.WriteLine($"  CACHE-HIT '{candidate.Name}' (cached {cachedResult.CachedAtUtc:u})");
                    cacheHits++;
                    yield return new ValidatedScanCandidate(
                        cachedResult.SteamAppId,
                        cachedResult.GameName,
                        cachedResult.RawgId,
                        cachedResult.ImportSource,
                        cachedResult.ExecutablePath,
                        cachedResult.FolderPath,
                        cachedResult.LaunchScriptPath);
                    continue;
                }

                // EXE detection funnel
                var exeSw = Stopwatch.StartNew();
                string executablePath;
                try
                {
                    executablePath = ExecutableDetector.ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name);
                }
                catch (Exception ex)
                {
                    exeSw.Stop();
                    exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                    exeDetectionCount++;
                    Debug.WriteLine($"  REJECT '{candidate.Name}' (exe-detect error {exeSw.ElapsedMilliseconds}ms): {ex.GetType().Name}: {ex.Message}");
                    rejectedNoExe++;
                    continue;
                }
                exeSw.Stop();
                exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                exeDetectionCount++;

                if (string.IsNullOrEmpty(executablePath))
                {
                    Debug.WriteLine($"  REJECT '{candidate.Name}' (no exe, exe-detect {exeSw.ElapsedMilliseconds}ms)");
                    rejectedNoExe++;
                    continue;
                }

                // Steam ID: existing, or lookup (skip for Riot — not on Steam, avoids fuzzy false matches)
                int? steamId = candidate.SteamAppId;
                bool isRiotSource = string.Equals(candidate.Source, "Riot Games", StringComparison.OrdinalIgnoreCase);

                if (!steamId.HasValue && !isRiotSource)
                {
                    var steamSw = Stopwatch.StartNew();
                    try
                    {
                        (int? foundSteamId, int? _, string? _) = await _gameName.FindGameIdsAsync(executablePath);
                        steamSw.Stop();
                        steamLookupTotalMs += steamSw.ElapsedMilliseconds;
                        steamLookupCount++;
                        if (foundSteamId.HasValue)
                        {
                            steamId = foundSteamId;
                            Debug.WriteLine($"  STEAM-LOOKUP '{candidate.Name}' -> id={steamId} ({steamSw.ElapsedMilliseconds}ms)");
                        }
                        else
                        {
                            Debug.WriteLine($"  STEAM-LOOKUP '{candidate.Name}' -> none ({steamSw.ElapsedMilliseconds}ms)");
                        }
                    }
                    catch (Exception ex)
                    {
                        steamSw.Stop();
                        steamLookupTotalMs += steamSw.ElapsedMilliseconds;
                        steamLookupCount++;
                        Debug.WriteLine($"  STEAM-LOOKUP '{candidate.Name}' FAILED ({steamSw.ElapsedMilliseconds}ms): {ex.Message}");
                    }
                }

                // RAWG validation — mode depends on Steam ID source
                // Launcher-provided Steam ID (e.g. SteamScanner): highest confidence → lenient threshold
                // Lookup-found Steam ID (heuristic candidate): moderate confidence → standard SteamBacked threshold
                var rawgMode = candidate.SteamAppId.HasValue
                    ? RawgValidationMode.HighConfidenceSteam
                    : steamId.HasValue
                        ? RawgValidationMode.SteamBacked
                        : RawgValidationMode.Strict;

                var rawgSw = Stopwatch.StartNew();
                int? rawgId = await ValidateAndFetchRawgIdAsync(candidate.Name, rawgMode);
                rawgSw.Stop();
                rawgValidationTotalMs += rawgSw.ElapsedMilliseconds;
                rawgValidationCount++;

                bool isFromLauncher = !candidate.Source.Equals("Heuristic Scan", StringComparison.OrdinalIgnoreCase);
                if (!rawgId.HasValue && !isFromLauncher)
                {
                    Debug.WriteLine($"  REJECT '{candidate.Name}' (no RAWG match, rawg {rawgSw.ElapsedMilliseconds}ms)");
                    rejectedNoRawg++;
                    continue;
                }

                Debug.WriteLine($"  VALIDATED '{candidate.Name}' steam={steamId?.ToString() ?? "-"} rawg={rawgId?.ToString() ?? "-"} (rawg {rawgSw.ElapsedMilliseconds}ms, exe {exeSw.ElapsedMilliseconds}ms)");
                newValidated++;
                scanCache.Upsert(candidate, candidate.Name, executablePath, steamId, rawgId, candidate.LaunchScriptPath);
                yield return new ValidatedScanCandidate(steamId, candidate.Name, rawgId, candidate.Source, executablePath, candidate.FolderPath, candidate.LaunchScriptPath);
            }

            phase3Sw.Stop();
            phase3Ms = phase3Sw.ElapsedMilliseconds;

            var saveSw = Stopwatch.StartNew();
            await scanCache.SaveAsync();
            saveSw.Stop();
            cacheSaveMs = saveSw.ElapsedMilliseconds;

            progress?.Report("Scan complete.");
            totalStopwatch.Stop();

            // Summary
            int totalFound = cacheHits + newValidated;
            int totalRejected = rejectedNoExe + rejectedNoRawg + skippedUtility;
            string phase1Breakdown = string.Join(", ",
                phase1Timings
                    .OrderByDescending(t => t.Ms)
                    .Select(t => $"{t.Name}: {t.Ms}ms ({t.Count})"));

            Debug.WriteLine("\n=== SCAN COMPLETE ===");
            Debug.WriteLine($"Total time:       {totalStopwatch.Elapsed.TotalSeconds:0.0}s");
            Debug.WriteLine($"Candidates:       {allCandidates.Count} unique (dedup removed {duplicateCount}, catalog removed {catalogFiltered})");
            Debug.WriteLine($"Games yielded:    {totalFound}");
            Debug.WriteLine($"  Cache hits:     {cacheHits}");
            Debug.WriteLine($"  New validated:  {newValidated}");
            Debug.WriteLine($"Rejected:         {totalRejected} (no-exe: {rejectedNoExe}, no-rawg: {rejectedNoRawg}, utility: {skippedUtility})");
            Debug.WriteLine($"Phase 1 time:     {phase1Sw.ElapsedMilliseconds}ms [{phase1Breakdown}]");
            Debug.WriteLine($"Phase 2 time:     {phase2Ms}ms ({phase2Count} candidates)");
            Debug.WriteLine($"Phase 3 time:     {phase3Ms}ms");
            if (exeDetectionCount > 0)
                Debug.WriteLine($"  ExeDetect:      {exeDetectionTotalMs}ms total, {exeDetectionTotalMs / exeDetectionCount}ms avg ({exeDetectionCount} calls)");
            if (steamLookupCount > 0)
                Debug.WriteLine($"  SteamLookup:    {steamLookupTotalMs}ms total, {steamLookupTotalMs / steamLookupCount}ms avg ({steamLookupCount} calls)");
            if (rawgValidationCount > 0)
                Debug.WriteLine($"  RawgValidate:   {rawgValidationTotalMs}ms total, {rawgValidationTotalMs / rawgValidationCount}ms avg ({rawgValidationCount} calls)");
            Debug.WriteLine($"Dedup/filter:     {dedupFilterMs}ms");
            Debug.WriteLine($"Cache load/save:  {cacheLoadMs}ms / {cacheSaveMs}ms");
            Debug.WriteLine("=====================");
        }

        public async Task<List<ValidatedScanCandidate>> ScanAllGamesAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var results = new List<ValidatedScanCandidate>();
            await foreach (var candidate in ScanIncrementallyAsync(cancellationToken, progress).ConfigureAwait(false))
            {
                results.Add(candidate);
            }

            return results;
        }

        private async Task<int?> ValidateAndFetchRawgIdAsync(string gameName, RawgValidationMode mode)
        {
            try
            {
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
