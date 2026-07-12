using Codec.Services.Fetching;
using Codec.Services.Logging;
using Codec.Services.Resolving;
using Codec.Services.Scanning.Scanners;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

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
        string? LaunchScriptPath = null,
        int? IgdbId = null,
        string? EpicAppId = null,
        ScanLogBatch? LogBatch = null,
        string? MetadataLookupName = null);

    public class GameScanner
    {
        private sealed class ValidationMetrics
        {
            public int CacheHits;
            public int NewValidated;
            public int RejectedNoExe;
            public int RejectedNoRawg;
            public int SkippedUtility;
            public long SteamLookupTotalMs;
            public int SteamLookupCount;
            public long RawgValidationTotalMs;
            public int RawgValidationCount;
            public long ExeDetectionTotalMs;
            public int ExeDetectionCount;
        }

        private readonly List<PlatformScanner> _platformScanners;
        private readonly HeuristicScanner _heuristicScanner;
        private readonly GameNameService _gameName;
        private readonly IgdbService _igdb;
        private readonly ScanResourceLimiter? _resourceLimiter;
        private readonly ScanConcurrencyOptions _concurrency;
        private readonly SteamScanner _steamScanner = new();
        private readonly EpicGamesScanner _epicScanner = new();

        public string? DetectedSteamClientPath => _steamScanner.DetectedSteamClientPath;
        public string? DetectedEpicLauncherPath => _epicScanner.DetectedEpicLauncherPath;

        public GameScanner(GameNameService gameName, IgdbService igdb, ScanResourceLimiter? resourceLimiter = null)
        {
            _gameName = gameName;
            _igdb = igdb;
            _resourceLimiter = resourceLimiter;
            _concurrency = resourceLimiter?.Options ?? ScanConcurrencyOptions.CreateAdaptive();
            _platformScanners = new List<PlatformScanner>
            {
                _steamScanner,
                _epicScanner,
                new RiotGamesScanner()
            };
            _heuristicScanner = new HeuristicScanner(resourceLimiter);
        }

        /// <summary>
        /// Execute complete 3-phase scan
        /// </summary>
        public async IAsyncEnumerable<ValidatedScanCandidate> ScanIncrementallyAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            IProgress<string>? progress = null,
            Stopwatch? clickStopwatch = null)
        {
            bool ownStopwatch = clickStopwatch is null;
            var totalStopwatch = clickStopwatch ?? Stopwatch.StartNew();
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
            int earlySteamYielded = 0;
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

            LogSession("=== STARTING COMPLETE GAME LIBRARY SCAN ===");
            LogSession($"Concurrency: heuristic={_concurrency.HeuristicWorkers}, validation={_concurrency.ValidationWorkers}, import={_concurrency.ImportWorkers}, network={_concurrency.NetworkOperations}, disk={_concurrency.DiskOperations}, folderSize={_concurrency.FolderSizeOperations}");
            progress?.Report("Starting comprehensive game scan...");

            // PHASE 1: High-Reliability Launcher Integration
            LogSession("\n=== PHASE 1: LAUNCHER INTEGRATION ===");
            var phase1Sw = Stopwatch.StartNew();
            var remainingPlatformTasks = _platformScanners
                .Where(scanner => !ReferenceEquals(scanner, _steamScanner))
                .Select(scanner => ScanPlatformScannerAsync(scanner, progress, cancellationToken))
                .ToList();
            var steamTask = ScanPlatformScannerAsync(_steamScanner, progress, cancellationToken);
            var steamScan = await steamTask.ConfigureAwait(false);
            var steamCandidates = steamScan.Candidates;
            phase1Timings.Add((_steamScanner.PlatformName, steamScan.ElapsedMs, steamScan.Count));

            var steamLibraryPaths = _steamScanner.KnownLibraryPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _heuristicScanner.SetExcludedPaths(steamLibraryPaths);
            LogSession($"  Steam library paths collected: {steamLibraryPaths.Count}");

            if (steamCandidates.Count > 0)
            {
                progress?.Report($"Queueing {steamCandidates.Count} Steam games while local scan continues...");
                foreach (var candidate in steamCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batch = new ScanLogBatch(candidate.Name, candidate.Source);

                    if (GameContentHeuristics.ShouldIgnoreCandidate(candidate.Name, candidate.FolderPath, candidate.Source, candidate.SteamAppId.HasValue))
                    {
                        batch.Flush("– SKIPPED", "utility/non-game heuristic");
                        skippedUtility++;
                        continue;
                    }

                    var exeSw = Stopwatch.StartNew();
                    string executablePath = TryGetSteamGameExe(candidate.FolderPath);
                    exeSw.Stop();
                    exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                    exeDetectionCount++;

                    if (string.IsNullOrEmpty(executablePath))
                    {
                        batch.Log($"STEAM-EARLY queued via steam URI (no root exe, {exeSw.ElapsedMilliseconds}ms)");
                    }
                    else
                    {
                        batch.Log($"STEAM-EARLY -> {Path.GetFileName(executablePath)} ({exeSw.ElapsedMilliseconds}ms)");
                    }

                    earlySteamYielded++;
                    scanCache.Upsert(candidate, candidate.Name, executablePath, candidate.SteamAppId, null, candidate.LaunchScriptPath);
                    yield return new ValidatedScanCandidate(
                        candidate.SteamAppId,
                        candidate.Name,
                        null,
                        candidate.Source,
                        executablePath,
                        candidate.FolderPath,
                        candidate.LaunchScriptPath,
                        IgdbId: null,
                        EpicAppId: candidate.EpicAppId,
                        LogBatch: batch,
                        MetadataLookupName: candidate.MetadataLookupName);
                }
            }

            var remainingPlatformScans = await Task.WhenAll(remainingPlatformTasks).ConfigureAwait(false);
            foreach (var scan in remainingPlatformScans)
            {
                phase1Timings.Add((scan.Scanner.PlatformName, scan.ElapsedMs, scan.Count));
                allCandidates.AddRange(scan.Candidates);
            }

            phase1Sw.Stop();

            var allLibraryPaths = _platformScanners
                .SelectMany(s => s.KnownLibraryPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _heuristicScanner.SetExcludedPaths(allLibraryPaths);
            LogSession($"  Platform library paths collected: {allLibraryPaths.Count}");

            LogSession("\n=== PHASE 2: HEURISTIC SCANNING ===");
            var phase2Sw = Stopwatch.StartNew();
            progress?.Report("Scanning standard installation directories...");
            var heuristicTask = _heuristicScanner.ScanAsync(progress, cancellationToken);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var heuristicCandidates = await heuristicTask.ConfigureAwait(false);
                phase2Count = heuristicCandidates.Count;
                allCandidates.AddRange(heuristicCandidates);
                LogSession($"  Heuristic: {phase2Count} potential games");
            }
            catch (Exception ex)
            {
                LogSession($"  Heuristic FAILED: {ex.Message}");
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

            LogSession($"\n  Dedup: removed {duplicateCount} duplicate folder paths");
            if (catalogFiltered > 0)
            {
                LogSession($"  Catalog filter: removed {catalogFiltered} utility entries");
            }
            LogSession($"  Unique candidates: {allCandidates.Count} ({dedupFilterMs}ms)");

            // PHASE 3: External Validation & Enrichment + EXE Detection
            LogSession("\n=== PHASE 3: VALIDATION & EXE DETECTION ===");
            progress?.Report($"Validating and analyzing {allCandidates.Count} candidates...");
            var phase3Sw = Stopwatch.StartNew();

            int processedCount = 0;

            if (_concurrency.ValidationWorkers > 1)
            {
                var metrics = new ValidationMetrics
                {
                    CacheHits = cacheHits,
                    NewValidated = newValidated,
                    RejectedNoExe = rejectedNoExe,
                    RejectedNoRawg = rejectedNoRawg,
                    SkippedUtility = skippedUtility,
                    SteamLookupTotalMs = steamLookupTotalMs,
                    SteamLookupCount = steamLookupCount,
                    RawgValidationTotalMs = rawgValidationTotalMs,
                    RawgValidationCount = rawgValidationCount,
                    ExeDetectionTotalMs = exeDetectionTotalMs,
                    ExeDetectionCount = exeDetectionCount
                };

                await foreach (var validated in ValidateCandidatesInParallelAsync(
                    allCandidates, scanCache, metrics, progress, cancellationToken).ConfigureAwait(false))
                {
                    yield return validated;
                }

                cacheHits = metrics.CacheHits;
                newValidated = metrics.NewValidated;
                rejectedNoExe = metrics.RejectedNoExe;
                rejectedNoRawg = metrics.RejectedNoRawg;
                skippedUtility = metrics.SkippedUtility;
                steamLookupTotalMs = metrics.SteamLookupTotalMs;
                steamLookupCount = metrics.SteamLookupCount;
                rawgValidationTotalMs = metrics.RawgValidationTotalMs;
                rawgValidationCount = metrics.RawgValidationCount;
                exeDetectionTotalMs = metrics.ExeDetectionTotalMs;
                exeDetectionCount = metrics.ExeDetectionCount;
            }
            else
            {
                foreach (var candidate in allCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processedCount++;
                    progress?.Report($"Validating {processedCount}/{allCandidates.Count}: {candidate.Name}");

                var batch = new ScanLogBatch(candidate.Name, candidate.Source);
                bool isHeuristicSource = IsHeuristicSource(candidate.Source);
                bool isFromLauncher = !isHeuristicSource;

                if (GameContentHeuristics.ShouldIgnoreCandidate(candidate.Name, candidate.FolderPath, candidate.Source, candidate.SteamAppId.HasValue))
                {
                    batch.Flush("– SKIPPED", "utility/non-game heuristic");
                    skippedUtility++;
                    continue;
                }

                if (scanCache.TryGetValid(candidate, out var cachedResult))
                {
                    bool cachedIsSteamSource = string.Equals(candidate.Source, "Steam", StringComparison.OrdinalIgnoreCase);
                    var cachedCopyrightYears = !string.IsNullOrEmpty(cachedResult.ExecutablePath)
                        ? _gameName.TryGetExeCopyrightYears(cachedResult.ExecutablePath)
                        : new HashSet<int>();

                    if (!cachedIsSteamSource && !cachedResult.IgdbId.HasValue && cachedCopyrightYears.Count > 0)
                    {
                        batch.Log("CACHE-STALE missing IGDB year validation");
                        scanCache.Invalidate(candidate.FolderPath);
                    }
                    else
                    {
                        if (cachedResult.SteamAppId.HasValue &&
                            !cachedIsSteamSource &&
                            !await _gameName.SteamAppMatchesLocalGameAsync(cachedResult.SteamAppId.Value, candidate.Name, cachedResult.ExecutablePath))
                        {
                            batch.Log($"CACHE-STALE rejected cached steam={cachedResult.SteamAppId}");
                            scanCache.Invalidate(candidate.FolderPath);
                        }
                        else
                        {
                            batch.Log($"CACHE-HIT (cached {cachedResult.CachedAtUtc:u})");
                            cacheHits++;
                            yield return new ValidatedScanCandidate(
                                cachedResult.SteamAppId,
                                cachedResult.GameName,
                                cachedResult.RawgId,
                                cachedResult.ImportSource,
                                cachedResult.ExecutablePath,
                                cachedResult.FolderPath,
                                cachedResult.LaunchScriptPath,
                                cachedResult.IgdbId,
                                cachedResult.EpicAppId,
                                LogBatch: batch,
                                MetadataLookupName: cachedResult.MetadataLookupName);
                            continue;
                        }
                    }
                }

                // EXE detection: Steam games get a cheap root check; only heuristic scan
                // candidates run the local executable funnel. Launcher/platform scanners
                // provide their own launch target and should not be rejected by exe detect.
                var exeSw = Stopwatch.StartNew();
                string executablePath;
                if (candidate.SteamAppId.HasValue)
                {
                    executablePath = TryGetSteamGameExe(candidate.FolderPath);
                    exeSw.Stop();
                    exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                    exeDetectionCount++;
                    if (string.IsNullOrEmpty(executablePath))
                    {
                        // Steam games launch via steam:// URI — exe not required.
                        batch.Log("STEAM-EXE (no exe at root, launching via steam URI)");
                    }
                    else
                    {
                        batch.Log($"STEAM-EXE -> {Path.GetFileName(executablePath)} ({exeSw.ElapsedMilliseconds}ms)");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(candidate.EpicAppId))
                {
                    executablePath = TryGetExecutableHint(candidate);
                    exeSw.Stop();
                    exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                    exeDetectionCount++;

                    if (string.IsNullOrEmpty(executablePath))
                    {
                        batch.Log($"EPIC-LAUNCH app='{candidate.EpicAppId}' (no executable hint)");
                    }
                    else
                    {
                        batch.Log($"EPIC-EXE -> {Path.GetFileName(executablePath)} ({exeSw.ElapsedMilliseconds}ms)");
                    }
                }
                else if (ShouldUseFullExecutableDetection(candidate))
                {
                    try
                    {
                        executablePath = ExecutableDetector.ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name);
                    }
                    catch (Exception ex)
                    {
                        exeSw.Stop();
                        exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                        exeDetectionCount++;
                        batch.Flush("✗ REJECTED", $"exe-detect error {exeSw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                        rejectedNoExe++;
                        continue;
                    }
                    exeSw.Stop();
                    exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                    exeDetectionCount++;
                    if (string.IsNullOrEmpty(executablePath))
                    {
                        batch.Flush("✗ REJECTED", $"no exe (exe-detect {exeSw.ElapsedMilliseconds}ms)");
                        rejectedNoExe++;
                        continue;
                    }
                }
                else
                {
                    executablePath = string.Empty;
                    exeSw.Stop();
                    exeDetectionTotalMs += exeSw.ElapsedMilliseconds;
                    exeDetectionCount++;

                    bool hasLaunchScript = !string.IsNullOrWhiteSpace(candidate.LaunchScriptPath)
                        && File.Exists(candidate.LaunchScriptPath);
                    if (!hasLaunchScript && !CanTrustMissingExecutable(candidate))
                    {
                        batch.Flush("✗ REJECTED", "no platform launch target");
                        rejectedNoExe++;
                        continue;
                    }

                    batch.Log(hasLaunchScript
                        ? $"PLATFORM-LAUNCH lnk='{candidate.LaunchScriptPath}' (no exe scan)"
                        : $"PLATFORM-LAUNCH source='{candidate.Source}' (no exe scan)");
                }

                if (GameContentHeuristics.IsBlockedExecutable(executablePath))
                {
                    batch.Flush("– SKIPPED", $"blocked exe '{Path.GetFileName(executablePath)}'");
                    continue;
                }

                var executableCopyright = !string.IsNullOrEmpty(executablePath)
                    ? _gameName.TryGetExeCopyrightInfo(executablePath)
                    : GameNameService.ExeCopyrightInfo.Empty;
                LogExecutableCopyright(batch, executablePath, executableCopyright);
                IReadOnlySet<int> executableCopyrightYears = executableCopyright.Years;

                int? steamId = candidate.SteamAppId;
                bool isRiotSource = string.Equals(candidate.Source, "Riot Games", StringComparison.OrdinalIgnoreCase);

                // Validation funnel:
                //  - Steam platform ID present → trusted; pipeline will use Steam+IGDB
                //  - Non-Steam → IGDB first; Steam search is only a fallback when no EXE copyright year exists
                int? igdbId = null;
                int? rawgId = null;
                var validateSw = Stopwatch.StartNew();
                bool igdbCallFailed = false;
                if (!steamId.HasValue && !isRiotSource)
                {
                    try
                    {
                        var (foundIgdbId, igdbReleaseYear) = await _igdb.FindIgdbMatchByNameAsync(candidate.Name, executableCopyrightYears).ConfigureAwait(false);
                        if (foundIgdbId.HasValue && executableCopyrightYears.Count > 0 && igdbReleaseYear.HasValue)
                        {
                            batch.Log($"IGDB-YEAR exe©{string.Join("/", executableCopyrightYears.Order())} igdb={igdbReleaseYear}");
                        }
                        igdbId = foundIgdbId;

                        if (igdbId.HasValue)
                        {
                            int? igdbSteamId = await _igdb.FindSteamIdByIgdbIdAsync(igdbId.Value).ConfigureAwait(false);
                            if (igdbSteamId.HasValue)
                            {
                                steamId = igdbSteamId;
                                batch.Log($"IGDB-STEAM igdb={igdbId} -> steam={steamId}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        batch.Log($"IGDB-VALIDATE FAILED: {ex.Message}");
                        igdbCallFailed = true;
                    }

                    if (!igdbId.HasValue && !igdbCallFailed)
                    {
                        if (executableCopyrightYears.Count > 0)
                        {
                            batch.Flush("✗ REJECTED", $"no IGDB release-year match for exe©{string.Join("/", executableCopyrightYears.Order())}");
                            rejectedNoRawg++;
                            continue;
                        }
                        else
                        {
                            if (isHeuristicSource)
                            {
                                var steamSw = Stopwatch.StartNew();
                                try
                                {
                                    (int? foundSteamId, string? _) = await _gameName.FindGameIdsAsync(executablePath, nameHint: candidate.Name);
                                    steamSw.Stop();
                                    steamLookupTotalMs += steamSw.ElapsedMilliseconds;
                                    steamLookupCount++;
                                    if (foundSteamId.HasValue)
                                    {
                                        var (steamNameMatches, fallbackSteamName) = await _gameName.TrySteamAppMatchLocalGameAsync(foundSteamId.Value, candidate.Name, executablePath);
                                        if (steamNameMatches)
                                        {
                                            steamId = foundSteamId;
                                            batch.Log($"STEAM-FALLBACK -> id={steamId} steamName='{fallbackSteamName ?? "?"}' ({steamSw.ElapsedMilliseconds}ms)");
                                        }
                                        else
                                        {
                                            batch.Log($"STEAM-FALLBACK rejected id={foundSteamId} reason=name-mismatch local='{candidate.Name}' steamName='{fallbackSteamName ?? "(not found)"}' ({steamSw.ElapsedMilliseconds}ms)");
                                        }
                                    }
                                    else
                                    {
                                        batch.Log($"STEAM-FALLBACK -> none ({steamSw.ElapsedMilliseconds}ms)");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    steamSw.Stop();
                                    steamLookupTotalMs += steamSw.ElapsedMilliseconds;
                                    steamLookupCount++;
                                    batch.Log($"STEAM-FALLBACK FAILED ({steamSw.ElapsedMilliseconds}ms): {ex.Message}");
                                }
                            }

                            if (!steamId.HasValue)
                            {
                                rawgId = await ValidateAndFetchRawgIdAsync(batch, candidate.Name, RawgValidationMode.Strict);
                            }
                        }
                    }
                }
                validateSw.Stop();
                rawgValidationTotalMs += validateSw.ElapsedMilliseconds;
                rawgValidationCount++;

                if (!steamId.HasValue && !igdbId.HasValue && !rawgId.HasValue && !isFromLauncher && !candidate.HasStrongGameSignals)
                {
                    batch.Flush("✗ REJECTED", $"no IGDB/RAWG match (validate {validateSw.ElapsedMilliseconds}ms)");
                    rejectedNoRawg++;
                    continue;
                }

                batch.Log($"VALIDATED steam={steamId?.ToString() ?? "-"} epic={candidate.EpicAppId ?? "-"} igdb={igdbId?.ToString() ?? "-"} rawg={rawgId?.ToString() ?? "-"} (validate {validateSw.ElapsedMilliseconds}ms, exe {exeSw.ElapsedMilliseconds}ms)");
                newValidated++;
                scanCache.Upsert(candidate, candidate.Name, executablePath, steamId, rawgId, candidate.LaunchScriptPath, igdbId);
                    yield return new ValidatedScanCandidate(
                    steamId,
                    candidate.Name,
                    rawgId,
                    candidate.Source,
                    executablePath,
                    candidate.FolderPath,
                    candidate.LaunchScriptPath,
                    igdbId,
                    candidate.EpicAppId,
                    LogBatch: batch,
                        MetadataLookupName: candidate.MetadataLookupName);
                }
            }

            phase3Sw.Stop();
            phase3Ms = phase3Sw.ElapsedMilliseconds;

            var saveSw = Stopwatch.StartNew();
            await scanCache.SaveAsync();
            saveSw.Stop();
            cacheSaveMs = saveSw.ElapsedMilliseconds;

            progress?.Report("Scan complete.");
            if (ownStopwatch)
                totalStopwatch.Stop();

            // Summary
            int totalFound = cacheHits + newValidated + earlySteamYielded;
            int totalRejected = rejectedNoExe + rejectedNoRawg + skippedUtility;
            string phase1Breakdown = string.Join(", ",
                phase1Timings
                    .OrderByDescending(t => t.Ms)
                    .Select(t => $"{t.Name}: {t.Ms}ms ({t.Count})"));

            LogSession("\n=== SCAN COMPLETE (pipeline still running) ===");
            LogSession($"Scan time:        {totalStopwatch.Elapsed.TotalSeconds:0.0}s");
            LogSession($"Candidates:       {allCandidates.Count} unique (dedup removed {duplicateCount}, catalog removed {catalogFiltered})");
            LogSession($"Games yielded:    {totalFound}");
            LogSession($"  Steam early:    {earlySteamYielded}");
            LogSession($"  Cache hits:     {cacheHits}");
            LogSession($"  New validated:  {newValidated}");
            LogSession($"Rejected:         {totalRejected} (no-exe: {rejectedNoExe}, no-rawg: {rejectedNoRawg}, utility: {skippedUtility})");
            LogSession($"Phase 1 time:     {phase1Sw.ElapsedMilliseconds}ms [{phase1Breakdown}]");
            LogSession($"Phase 2 time:     {phase2Ms}ms ({phase2Count} candidates)");
            LogSession($"Phase 3 time:     {phase3Ms}ms");
            if (exeDetectionCount > 0)
                LogSession($"  ExeDetect:      {exeDetectionTotalMs}ms total, {exeDetectionTotalMs / exeDetectionCount}ms avg ({exeDetectionCount} calls)");
            if (steamLookupCount > 0)
                LogSession($"  SteamLookup:    {steamLookupTotalMs}ms total, {steamLookupTotalMs / steamLookupCount}ms avg ({steamLookupCount} calls)");
            if (rawgValidationCount > 0)
                LogSession($"  RawgValidate:   {rawgValidationTotalMs}ms total, {rawgValidationTotalMs / rawgValidationCount}ms avg ({rawgValidationCount} calls)");
            LogSession($"Dedup/filter:     {dedupFilterMs}ms");
            LogSession($"Cache load/save:  {cacheLoadMs}ms / {cacheSaveMs}ms");
            LogSession("=====================");
        }

        private async IAsyncEnumerable<ValidatedScanCandidate> ValidateCandidatesInParallelAsync(
            IReadOnlyList<GameCandidate> candidates,
            ScanCache scanCache,
            ValidationMetrics metrics,
            IProgress<string>? progress,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var output = Channel.CreateBounded<ValidatedScanCandidate>(new BoundedChannelOptions(_concurrency.ValidationBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            int processed = 0;

            Task producer = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    var options = new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = _concurrency.ValidationWorkers
                    };

                    await Parallel.ForEachAsync(candidates, options, async (candidate, ct) =>
                    {
                        int current = Interlocked.Increment(ref processed);
                        progress?.Report($"Validating {current}/{candidates.Count}: {candidate.Name}");
                        var result = await ValidateCandidateAsync(candidate, scanCache, metrics, ct).ConfigureAwait(false);
                        if (result != null)
                        {
                            await output.Writer.WriteAsync(result, ct).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    output.Writer.TryComplete(failure);
                }
            }, cancellationToken);

            await foreach (var result in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }

            await producer.ConfigureAwait(false);
        }

        private async Task<ValidatedScanCandidate?> ValidateCandidateAsync(
            GameCandidate candidate,
            ScanCache scanCache,
            ValidationMetrics metrics,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new ScanLogBatch(candidate.Name, candidate.Source);
            bool isHeuristicSource = IsHeuristicSource(candidate.Source);
            bool isFromLauncher = !isHeuristicSource;

            if (GameContentHeuristics.ShouldIgnoreCandidate(candidate.Name, candidate.FolderPath, candidate.Source, candidate.SteamAppId.HasValue))
            {
                batch.Flush("– SKIPPED", "utility/non-game heuristic");
                Interlocked.Increment(ref metrics.SkippedUtility);
                return null;
            }

            if (scanCache.TryGetValid(candidate, out var cachedResult))
            {
                bool cachedIsSteamSource = string.Equals(candidate.Source, "Steam", StringComparison.OrdinalIgnoreCase);
                var cachedCopyrightYears = !string.IsNullOrEmpty(cachedResult.ExecutablePath)
                    ? _gameName.TryGetExeCopyrightYears(cachedResult.ExecutablePath)
                    : new HashSet<int>();

                if (!cachedIsSteamSource && !cachedResult.IgdbId.HasValue && cachedCopyrightYears.Count > 0)
                {
                    batch.Log("CACHE-STALE missing IGDB year validation");
                    scanCache.Invalidate(candidate.FolderPath);
                }
                else if (cachedResult.SteamAppId.HasValue && !cachedIsSteamSource &&
                         !await _gameName.SteamAppMatchesLocalGameAsync(cachedResult.SteamAppId.Value, candidate.Name, cachedResult.ExecutablePath).ConfigureAwait(false))
                {
                    batch.Log($"CACHE-STALE rejected cached steam={cachedResult.SteamAppId}");
                    scanCache.Invalidate(candidate.FolderPath);
                }
                else
                {
                    batch.Log($"CACHE-HIT (cached {cachedResult.CachedAtUtc:u})");
                    Interlocked.Increment(ref metrics.CacheHits);
                    return new ValidatedScanCandidate(
                        cachedResult.SteamAppId, cachedResult.GameName, cachedResult.RawgId,
                        cachedResult.ImportSource, cachedResult.ExecutablePath, cachedResult.FolderPath,
                        cachedResult.LaunchScriptPath, cachedResult.IgdbId, cachedResult.EpicAppId,
                        batch, cachedResult.MetadataLookupName);
                }
            }

            var exeSw = Stopwatch.StartNew();
            string executablePath;
            try
            {
                if (candidate.SteamAppId.HasValue)
                {
                    executablePath = TryGetSteamGameExe(candidate.FolderPath);
                }
                else if (!string.IsNullOrWhiteSpace(candidate.EpicAppId))
                {
                    executablePath = TryGetExecutableHint(candidate);
                }
                else if (ShouldUseFullExecutableDetection(candidate))
                {
                    executablePath = _resourceLimiter is null
                        ? ExecutableDetector.ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name)
                        : await _resourceLimiter.RunDiskAsync(ct => Task.Run(
                            () => ExecutableDetector.ExecuteDetectionFunnel(candidate.FolderPath, candidate.Name), ct),
                            cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    executablePath = string.Empty;
                    bool hasLaunchScript = !string.IsNullOrWhiteSpace(candidate.LaunchScriptPath) && File.Exists(candidate.LaunchScriptPath);
                    if (!hasLaunchScript && !CanTrustMissingExecutable(candidate))
                    {
                        batch.Flush("✗ REJECTED", "no platform launch target");
                        Interlocked.Increment(ref metrics.RejectedNoExe);
                        return null;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                batch.Flush("✗ REJECTED", $"exe-detect error: {ex.GetType().Name}: {ex.Message}");
                Interlocked.Increment(ref metrics.RejectedNoExe);
                return null;
            }
            finally
            {
                exeSw.Stop();
                Interlocked.Add(ref metrics.ExeDetectionTotalMs, exeSw.ElapsedMilliseconds);
                Interlocked.Increment(ref metrics.ExeDetectionCount);
            }

            if (ShouldUseFullExecutableDetection(candidate) && string.IsNullOrEmpty(executablePath))
            {
                batch.Flush("✗ REJECTED", $"no exe (exe-detect {exeSw.ElapsedMilliseconds}ms)");
                Interlocked.Increment(ref metrics.RejectedNoExe);
                return null;
            }
            if (GameContentHeuristics.IsBlockedExecutable(executablePath))
            {
                batch.Flush("– SKIPPED", $"blocked exe '{Path.GetFileName(executablePath)}'");
                return null;
            }

            var executableCopyright = !string.IsNullOrEmpty(executablePath)
                ? _gameName.TryGetExeCopyrightInfo(executablePath)
                : GameNameService.ExeCopyrightInfo.Empty;
            LogExecutableCopyright(batch, executablePath, executableCopyright);

            int? steamId = candidate.SteamAppId;
            int? igdbId = null;
            int? rawgId = null;
            bool isRiotSource = string.Equals(candidate.Source, "Riot Games", StringComparison.OrdinalIgnoreCase);
            bool igdbYearRejected = false;
            var validateSw = Stopwatch.StartNew();

            try
            {
                async Task ResolveExternalAsync(CancellationToken ct)
                {
                    bool igdbCallFailed = false;
                    if (!steamId.HasValue && !isRiotSource)
                    {
                        try
                        {
                            var match = await _igdb.FindIgdbMatchByNameAsync(candidate.Name, executableCopyright.Years).ConfigureAwait(false);
                            igdbId = match.Id;
                            if (igdbId.HasValue)
                            {
                                steamId = await _igdb.FindSteamIdByIgdbIdAsync(igdbId.Value).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            batch.Log($"IGDB-VALIDATE FAILED: {ex.Message}");
                            igdbCallFailed = true;
                        }

                        if (!igdbId.HasValue && !igdbCallFailed)
                        {
                            if (executableCopyright.Years.Count > 0)
                            {
                                igdbYearRejected = true;
                                return;
                            }

                            if (isHeuristicSource)
                            {
                                var steamSw = Stopwatch.StartNew();
                                try
                                {
                                    var found = await _gameName.FindGameIdsAsync(executablePath, nameHint: candidate.Name).ConfigureAwait(false);
                                    if (found.steamId.HasValue)
                                    {
                                        var checkedMatch = await _gameName.TrySteamAppMatchLocalGameAsync(found.steamId.Value, candidate.Name, executablePath).ConfigureAwait(false);
                                        if (checkedMatch.Matches) steamId = found.steamId;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    batch.Log($"STEAM-FALLBACK FAILED: {ex.Message}");
                                }
                                finally
                                {
                                    steamSw.Stop();
                                    Interlocked.Add(ref metrics.SteamLookupTotalMs, steamSw.ElapsedMilliseconds);
                                    Interlocked.Increment(ref metrics.SteamLookupCount);
                                }
                            }

                            if (!steamId.HasValue)
                            {
                                rawgId = await ValidateAndFetchRawgIdAsync(batch, candidate.Name, RawgValidationMode.Strict).ConfigureAwait(false);
                            }
                        }
                    }
                }

                await ResolveExternalAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                validateSw.Stop();
                Interlocked.Add(ref metrics.RawgValidationTotalMs, validateSw.ElapsedMilliseconds);
                Interlocked.Increment(ref metrics.RawgValidationCount);
            }

            if (igdbYearRejected)
            {
                batch.Flush("✗ REJECTED", $"no IGDB release-year match for exe©{string.Join("/", executableCopyright.Years.Order())}");
                Interlocked.Increment(ref metrics.RejectedNoRawg);
                return null;
            }

            if (!steamId.HasValue && !igdbId.HasValue && !rawgId.HasValue &&
                !isFromLauncher && !candidate.HasStrongGameSignals)
            {
                batch.Flush("✗ REJECTED", $"no IGDB/RAWG match (validate {validateSw.ElapsedMilliseconds}ms)");
                Interlocked.Increment(ref metrics.RejectedNoRawg);
                return null;
            }

            batch.Log($"VALIDATED steam={steamId?.ToString() ?? "-"} epic={candidate.EpicAppId ?? "-"} igdb={igdbId?.ToString() ?? "-"} rawg={rawgId?.ToString() ?? "-"}");
            scanCache.Upsert(candidate, candidate.Name, executablePath, steamId, rawgId, candidate.LaunchScriptPath, igdbId);
            Interlocked.Increment(ref metrics.NewValidated);
            return new ValidatedScanCandidate(
                steamId, candidate.Name, rawgId, candidate.Source, executablePath,
                candidate.FolderPath, candidate.LaunchScriptPath, igdbId, candidate.EpicAppId,
                batch, candidate.MetadataLookupName);
        }

        internal static void LogSession(string line)
        {
            Debug.WriteLine(line);
            ScanLogFile.WriteSession(line);
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

        internal static bool ShouldUseFullExecutableDetection(GameCandidate candidate) =>
            !candidate.SteamAppId.HasValue &&
            string.IsNullOrWhiteSpace(candidate.EpicAppId) &&
            IsHeuristicSource(candidate.Source);

        internal static bool CanTrustMissingExecutable(GameCandidate candidate) =>
            candidate.SteamAppId.HasValue ||
            !string.IsNullOrWhiteSpace(candidate.EpicAppId) ||
            string.Equals(candidate.Source, "Riot Games", StringComparison.OrdinalIgnoreCase);

        internal static bool CanTrustMissingExecutable(string? source) =>
            string.Equals(source, "Riot Games", StringComparison.OrdinalIgnoreCase);

        private static bool IsHeuristicSource(string? source) =>
            string.Equals(source, "Heuristic Scan", StringComparison.OrdinalIgnoreCase);

        private static async Task<(PlatformScanner Scanner, List<GameCandidate> Candidates, long ElapsedMs, int Count)> ScanPlatformScannerAsync(
            PlatformScanner scanner,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var scannerSw = Stopwatch.StartNew();
            int count = 0;
            var candidates = new List<GameCandidate>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {scanner.PlatformName}...");
                candidates = await scanner.ScanAsync(progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                count = candidates.Count;
                scannerSw.Stop();
                LogSession($"  {scanner.PlatformName}: {count} games in {scannerSw.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException)
            {
                scannerSw.Stop();
                throw;
            }
            catch (Exception ex)
            {
                scannerSw.Stop();
                LogSession($"  {scanner.PlatformName} FAILED in {scannerSw.ElapsedMilliseconds}ms: {ex.Message}");
                progress?.Report($"Warning: {scanner.PlatformName} scan failed");
            }

            return (scanner, candidates, scannerSw.ElapsedMilliseconds, count);
        }

        private async Task<int?> ValidateAndFetchRawgIdAsync(ScanLogBatch batch, string gameName, RawgValidationMode mode)
        {
            try
            {
                return await _gameName.FindRawgIdByNameAsync(gameName, mode);
            }
            catch (Exception ex)
            {
                batch.Log($"RAWG validation failed: {ex.Message}");
                return null;
            }
        }

        private static void LogExecutableCopyright(ScanLogBatch batch, string executablePath, GameNameService.ExeCopyrightInfo copyright)
        {
            string exeName = string.IsNullOrWhiteSpace(executablePath)
                ? "-"
                : Path.GetFileName(executablePath);
            string years = copyright.Years.Count > 0
                ? string.Join("/", copyright.Years.Order())
                : "-";
            string text = string.IsNullOrWhiteSpace(copyright.Text)
                ? "-"
                : TruncateForDebug(copyright.Text!, 260);

            batch.Log($"EXE-COPYRIGHT exe='{exeName}' source={copyright.Source} years={years} text=\"{text}\"");
        }

        private static string TruncateForDebug(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength).TrimEnd() + "...";
        }

        private static string TryGetExecutableHint(GameCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.ExecutableHintPath))
            {
                return string.Empty;
            }

            try
            {
                string normalized = Path.GetFullPath(candidate.ExecutableHintPath);
                return File.Exists(normalized) ? normalized : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Fast exe resolution for Steam games: scans root of install dir only, picks largest
        /// non-utility exe. No recursive heuristic funnel needed — Steam handles launching.
        /// </summary>
        private static string TryGetSteamGameExe(string folderPath)
        {
            if (!Directory.Exists(folderPath))
                return string.Empty;

            try
            {
                var skip = new[] { "uninstall", "setup", "install", "vcredist", "vc_redist", "directx", "dxsetup", "crashpad", "crashreport", "crashhandler", "unitycrashhandler", "redist" };
                var exes = Directory.GetFiles(folderPath, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !skip.Any(s => Path.GetFileNameWithoutExtension(f).Contains(s, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToList();

                return exes.Count > 0 ? exes[0] : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
