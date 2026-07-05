using Codec.Helpers;
using Codec.Models;
using Codec.Services.Fetching;
using Codec.Services.Logging;
using Codec.Services.Resolving;
using Codec.Services.Scanning;
using Codec.Services;
using Codec.Services.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Importing
{
    public sealed class GameImportPipeline : IGameImportPipeline
    {
        private readonly GameNameService _gameName;
        private readonly GameDetailsService _gameDetails;
        private readonly SteamDetailsService _steamDetails;
        private readonly RawgDetailsService _rawgDetails;
        private readonly IgdbService _igdb;
        private readonly HltbService _hltb;
        private readonly DisplayedAssetService _displayedAssets;

        public GameImportPipeline(
            GameNameService gameName,
            GameDetailsService gameDetails,
            SteamDetailsService steamDetails,
            RawgDetailsService rawgDetails,
            IgdbService igdb,
            HltbService hltb,
            DisplayedAssetService displayedAssets)
        {
            _gameName = gameName;
            _gameDetails = gameDetails;
            _steamDetails = steamDetails;
            _rawgDetails = rawgDetails;
            _igdb = igdb;
            _hltb = hltb;
            _displayedAssets = displayedAssets;
        }

        public async Task<GameImportResult> ImportAsync(GameImportRequest request, IReadOnlyCollection<Game> librarySnapshot, CancellationToken cancellationToken = default)
        {
            string normalizedImportSource = PlatformSourceNames.NormalizeImportSource(request.ImportSource);
            var batch = request.LogBatch ?? new ScanLogBatch(request.NameHint, normalizedImportSource);

            batch.Log($"PIPELINE ENTRY exe='{request.ExecutablePath}' lnk='{request.LaunchScriptPath}' steam={request.SteamAppId} epic={request.EpicAppId} rawg={request.RawgId}");
            cancellationToken.ThrowIfCancellationRequested();

            bool hasLaunchScript = !string.IsNullOrWhiteSpace(request.LaunchScriptPath)
                && File.Exists(request.LaunchScriptPath);

            // Steam and Riot launcher games do not require a directly detected exe.
            bool isRiotSource = string.Equals(normalizedImportSource, PlatformSourceNames.RiotGames, StringComparison.OrdinalIgnoreCase);
            bool allowsMissingExecutable = AllowsMissingExecutable(request, hasLaunchScript);

            string normalizedExePath = string.Empty;
            if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
            {
                try
                {
                    normalizedExePath = Path.GetFullPath(request.ExecutablePath);
                }
                catch
                {
                    if (!allowsMissingExecutable)
                    {
                        batch.Flush("✗ INVALID", $"bad path: '{request.ExecutablePath}'");
                        return GameImportResult.Invalid("The selected executable path is invalid.");
                    }
                    batch.Log($"PIPELINE WARN (bad exe path ignored due to launcher target): '{request.ExecutablePath}'");
                    normalizedExePath = string.Empty;
                }
            }
            else if (!allowsMissingExecutable)
            {
                batch.Flush("✗ INVALID", $"empty exe (name='{request.NameHint}')");
                return GameImportResult.Invalid("No executable was selected.");
            }

            if (!string.IsNullOrEmpty(normalizedExePath) && !File.Exists(normalizedExePath))
            {
                if (!allowsMissingExecutable)
                {
                    batch.Flush("✗ INVALID", $"exe missing: '{normalizedExePath}'");
                    return GameImportResult.Invalid("The selected executable no longer exists.");
                }
                batch.Log($"PIPELINE WARN (exe missing, launcher target will be used): '{normalizedExePath}'");
                normalizedExePath = string.Empty;
            }

            if (!string.IsNullOrEmpty(normalizedExePath) && GameContentHeuristics.PathMatchesUtility(normalizedExePath))
            {
                if (!allowsMissingExecutable)
                {
                    batch.Flush("✗ INVALID", $"utility path: '{normalizedExePath}'");
                    return GameImportResult.Invalid("Codec rejected this executable because it looks like a launcher or utility.");
                }
                batch.Log($"PIPELINE WARN (utility exe ignored due to launcher target): '{normalizedExePath}'");
                normalizedExePath = string.Empty;
            }

            if (!string.IsNullOrEmpty(normalizedExePath)
                && librarySnapshot.Any(g => string.Equals(g.Executable, normalizedExePath, StringComparison.OrdinalIgnoreCase)))
            {
                batch.Flush("⤼ DUPLICATE", $"exe already in library: '{normalizedExePath}'");
                return GameImportResult.Duplicate("This executable is already in your library.");
            }

            string folderLocation = !string.IsNullOrWhiteSpace(request.FolderLocation)
                ? request.FolderLocation
                : (!string.IsNullOrEmpty(normalizedExePath) ? Path.GetDirectoryName(normalizedExePath) ?? string.Empty : string.Empty);

            if (RiotGameDuplicateHelper.IsDuplicateGame(normalizedImportSource, folderLocation, request.LaunchScriptPath, librarySnapshot))
            {
                batch.Flush("⤼ DUPLICATE", $"Riot target already in library: folder='{folderLocation}' lnk='{request.LaunchScriptPath}'");
                return GameImportResult.Duplicate("This Riot game is already in your library.");
            }

            string detectedName = string.IsNullOrWhiteSpace(request.NameHint)
                ? (!string.IsNullOrEmpty(normalizedExePath)
                    ? _gameName.GetBestName(normalizedExePath) ?? Path.GetFileNameWithoutExtension(normalizedExePath)
                    : string.Empty)
                : request.NameHint.Trim();

            if (string.IsNullOrWhiteSpace(detectedName) && !string.IsNullOrEmpty(normalizedExePath))
            {
                detectedName = Path.GetFileNameWithoutExtension(normalizedExePath);
            }

            detectedName = GameNameCleaner.RemoveTrailingDomainTag(detectedName);

            // Normalize ASCII trademark notation to unicode for display/storage
            detectedName = Regex.Replace(detectedName, @"\(TM\)", "™", RegexOptions.IgnoreCase);
            detectedName = Regex.Replace(detectedName, @"\(R\)", "®", RegexOptions.IgnoreCase);

            if (GameContentHeuristics.NameMatchesUtility(detectedName))
            {
                batch.Flush("✗ INVALID", $"utility name: '{detectedName}'");
                return GameImportResult.Invalid($"Codec rejected '{detectedName}' because it looks like a launcher or utility.");
            }

            try
            {
                int? steamId = request.SteamAppId;
                int? rawgId = request.RawgId;
                int? igdbId = request.IgdbId;
                var executableCopyright = !string.IsNullOrEmpty(normalizedExePath)
                    ? _gameName.TryGetExeCopyrightInfo(normalizedExePath)
                    : GameNameService.ExeCopyrightInfo.Empty;
                LogExecutableCopyright(batch, normalizedExePath, executableCopyright);
                IReadOnlySet<int> executableCopyrightYears = executableCopyright.Years;

                bool isSteamLauncherSource = string.Equals(normalizedImportSource, PlatformSourceNames.Steam, StringComparison.OrdinalIgnoreCase);

                // Non-Steam path: IGDB is the primary authority. If EXE copyright years exist,
                // they are compared only against IGDB release years.
                if (!steamId.HasValue && !igdbId.HasValue && !rawgId.HasValue && !string.IsNullOrWhiteSpace(detectedName))
                {
                    try
                    {
                        var (foundIgdbId, igdbReleaseYear) = await _igdb.FindIgdbMatchByNameAsync(detectedName, executableCopyrightYears).ConfigureAwait(false);
                        igdbId = foundIgdbId;
                        if (igdbId.HasValue && executableCopyrightYears.Count > 0 && igdbReleaseYear.HasValue)
                        {
                            batch.Log($"PIPELINE IGDB-YEAR exe©{string.Join("/", executableCopyrightYears.Order())} igdb={igdbReleaseYear}");
                        }
                        else if (!igdbId.HasValue && executableCopyrightYears.Count > 0 && !isRiotSource)
                        {
                            batch.Flush("✗ INVALID", $"no IGDB release-year match (exe©{string.Join("/", executableCopyrightYears.Order())})");
                            return GameImportResult.Invalid($"Codec rejected '{detectedName}' because its executable copyright year did not match an IGDB release year.");
                        }
                    }
                    catch (Exception ex)
                    {
                        batch.Log($"PIPELINE IGDB-VALIDATE FAILED: {ex.Message}");
                    }
                }

                if (!steamId.HasValue && igdbId.HasValue && !isRiotSource)
                {
                    steamId = await _igdb.FindSteamIdByIgdbIdAsync(igdbId.Value).ConfigureAwait(false);
                    if (steamId.HasValue)
                    {
                        batch.Log($"PIPELINE IGDB-STEAM igdb={igdbId} -> steam={steamId}");
                        var (steamNameMatches, igdbDerivedSteamName) = await _gameName.TrySteamAppMatchLocalGameAsync(steamId.Value, detectedName, normalizedExePath).ConfigureAwait(false);
                        if (!steamNameMatches)
                        {
                            batch.Log($"PIPELINE DROP-IGDB-STEAM-ID igdb={igdbId} steam={steamId} reason=name-mismatch local='{detectedName}' steamName='{igdbDerivedSteamName ?? "(not found)"}'");
                            steamId = null;
                        }
                        else if (librarySnapshot.Any(g => g.SteamID == steamId.Value))
                        {
                            batch.Flush("⤼ DUPLICATE", $"igdb-derived steam id {steamId} already in library");
                            return GameImportResult.Duplicate($"A game with Steam ID {steamId.Value} already exists in your library.");
                        }
                    }
                }

                // Steam search is a fallback only when IGDB could not resolve and no local copyright
                // year exists to validate against IGDB.
                if (!steamId.HasValue && !isRiotSource && !rawgId.HasValue && !igdbId.HasValue && executableCopyrightYears.Count == 0)
                {
                    var resolvedIds = await _gameName.FindGameIdsAsync(normalizedExePath, nameHint: detectedName).ConfigureAwait(false);
                    steamId ??= resolvedIds.steamId;
                    if (!string.IsNullOrWhiteSpace(resolvedIds.steamName))
                        detectedName = resolvedIds.steamName;
                }

                if (steamId.HasValue && !isSteamLauncherSource)
                {
                    var (steamNameMatches, droppedSteamName) = await _gameName.TrySteamAppMatchLocalGameAsync(steamId.Value, detectedName, normalizedExePath).ConfigureAwait(false);
                    if (!steamNameMatches)
                    {
                        batch.Log($"PIPELINE DROP-STEAM-ID source='{request.ImportSource}' steam={steamId} reason=name-mismatch local='{detectedName}' steamName='{droppedSteamName ?? "(not found)"}'");
                        steamId = null;
                        rawgId = null;
                        igdbId = null;
                    }
                }

                if (!steamId.HasValue && !igdbId.HasValue && !rawgId.HasValue && !isRiotSource && executableCopyrightYears.Count == 0)
                {
                    rawgId = await _gameDetails.ValidateGameAsync(detectedName, RawgValidationMode.Strict).ConfigureAwait(false);
                }

                if (steamId.HasValue && librarySnapshot.Any(g => g.SteamID == steamId.Value))
                {
                    batch.Flush("⤼ DUPLICATE", $"steam id {steamId} already in library");
                    return GameImportResult.Duplicate($"A game with Steam ID {steamId.Value} already exists in your library.");
                }

                if (!string.IsNullOrWhiteSpace(request.EpicAppId) &&
                    librarySnapshot.Any(g => string.Equals(g.EpicAppId, request.EpicAppId, StringComparison.OrdinalIgnoreCase)))
                {
                    batch.Flush("⤼ DUPLICATE", $"epic id {request.EpicAppId} already in library");
                    return GameImportResult.Duplicate($"A game with Epic ID {request.EpicAppId} already exists in your library.");
                }

                if (rawgId.HasValue && librarySnapshot.Any(g => g.RawgID == rawgId.Value))
                {
                    batch.Flush("⤼ DUPLICATE", $"rawg id {rawgId} already in library");
                    return GameImportResult.Duplicate($"A game with RAWG ID {rawgId.Value} already exists in your library.");
                }

                if (igdbId.HasValue && librarySnapshot.Any(g => g.IgdbId == igdbId.Value))
                {
                    batch.Flush("⤼ DUPLICATE", $"igdb id {igdbId} already in library");
                    return GameImportResult.Duplicate($"A game with IGDB ID {igdbId.Value} already exists in your library.");
                }

                var game = new Game
                {
                    Name = detectedName,
                    Executable = normalizedExePath,
                    FolderLocation = folderLocation,
                    ImportedFrom = normalizedImportSource,
                    SteamID = steamId,
                    EpicAppId = string.IsNullOrWhiteSpace(request.EpicAppId) ? null : request.EpicAppId,
                    RawgID = rawgId,
                    IgdbId = igdbId,
                    LaunchScript = hasLaunchScript
                        ? request.LaunchScriptPath
                        : null
                };

                if (Directory.Exists(folderLocation))
                {
                    try
                    {
                        game.FolderSize = await FolderSizeService.CalculateAsync(folderLocation).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        batch.Log($"PIPELINE folder size lookup failed: {ex.Message}");
                    }
                }

                if (game.SteamID.HasValue)
                {
                    // Steam first (assets, release date), then HLTB (preferred TTB source), then IGDB (fills gaps)
                    await _steamDetails.PopulateFromSteamAsync(game).ConfigureAwait(false);
                    await _hltb.PopulateAsync(game).ConfigureAwait(false);

                    var igdbIdTask = game.IgdbId.HasValue
                        ? Task.FromResult<int?>(game.IgdbId.Value)
                        : _igdb.FindIgdbIdBySteamIdAsync(game.SteamID.Value);
                    var rawgIdTask = _gameDetails.FindRawgIdBySteamIdAsync(game.SteamID.Value);
                    await Task.WhenAll(igdbIdTask, rawgIdTask).ConfigureAwait(false);

                    game.IgdbId = igdbIdTask.Result;
                    game.RawgID = rawgIdTask.Result;

                    if (game.IgdbId.HasValue)
                    {
                        await _igdb.PopulateFromIgdbAsync(game).ConfigureAwait(false);
                    }

                    // RAWG populate (non-validating) — fills RawgSlug/RawgUrl for link display; Steam/IGDB fields protected by ShouldOverwrite
                    if (game.RawgID.HasValue)
                    {
                        await _rawgDetails.PopulateAsync(game).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Non-Steam: HLTB first (preferred TTB source), then IGDB for metadata, then RAWG always to store RawgID
                    await _hltb.PopulateAsync(game).ConfigureAwait(false);

                    if (game.IgdbId.HasValue)
                    {
                        await _igdb.PopulateFromIgdbAsync(game).ConfigureAwait(false);
                    }

                    if (game.RawgID.HasValue)
                    {
                        await _rawgDetails.PopulateAsync(game).ConfigureAwait(false);
                    }
                    else
                    {
                        await _rawgDetails.TryPopulateRawgFromSearchAsync(game).ConfigureAwait(false);
                    }
                }

                FinalizeFallbackLinks(game);

                var displayedAssets = await _displayedAssets.EnsureDisplayedAssetsAsync(game).ConfigureAwait(false);
                ApplyDisplayedAssetHydration(game, displayedAssets);
                FinalizeFallbackLinks(game);

                bool isFromPlatformScanner = !string.Equals(normalizedImportSource, PlatformSourceNames.HeuristicScan, StringComparison.OrdinalIgnoreCase)
                    && !request.IsManual;

                if (!displayedAssets.AreRequiredAssetsReady && !isFromPlatformScanner)
                {
                    batch.Flush("✗ FAILED", $"assets not ready (cover={displayedAssets.IsCoverCached} hero={displayedAssets.IsHeroCached}/{displayedAssets.HasHeroSource} logo={displayedAssets.IsLogoCached}/{displayedAssets.HasLogoSource})");
                    return GameImportResult.Failed($"Codec could not finish downloading the required artwork for {game.Name}.");
                }

                game.IsFullyImported = true;
                batch.Flush("✓ ADDED", $"steam={game.SteamID} epic={game.EpicAppId} igdb={game.IgdbId} rawg={game.RawgID} lnk={game.LaunchScript}");
                return GameImportResult.Added(game, $"{game.Name} was added to your library.");
            }
            catch (Exception ex)
            {
                batch.Log($"PIPELINE EXCEPTION {ex.GetType().Name}: {ex.Message}");
                batch.Log(ex.StackTrace ?? string.Empty);
                batch.Flush("✗ FAILED", $"exception: {ex.GetType().Name}: {ex.Message}");
                return GameImportResult.Failed("Codec could not finish importing this game.");
            }
        }

        private static void FinalizeFallbackLinks(Game game)
        {
            if (game.RawgID.HasValue && string.IsNullOrWhiteSpace(game.RawgUrl))
            {
                game.RawgUrl = !string.IsNullOrWhiteSpace(game.RawgSlug)
                    ? $"https://rawg.io/games/{game.RawgSlug}"
                    : $"https://rawg.io/games/{game.RawgID.Value}";
            }
        }

        private static void ApplyDisplayedAssetHydration(Game game, DisplayedAssetService.DisplayedAssetHydrationResult hydration)
        {
            game.GridDbId = hydration.GridDbId ?? game.GridDbId;
            game.LibraryCapsuleCache = hydration.CapsuleCachePath;
            game.HasHeroAssetSource = hydration.HasHeroSource;
            game.LibraryHeroUrl = hydration.HeroUrl;
            game.LibraryHeroCache = hydration.HeroCachePath;
            game.HasLogoAssetSource = hydration.HasLogoSource;
            game.LibraryLogoUrl = hydration.LogoUrl;
            game.LibraryLogoCache = hydration.LogoCachePath;
        }

        internal static bool AllowsMissingExecutable(GameImportRequest request, bool hasLaunchScript) =>
            request.SteamAppId.HasValue ||
            !string.IsNullOrWhiteSpace(request.EpicAppId) ||
            hasLaunchScript ||
            string.Equals(PlatformSourceNames.NormalizeImportSource(request.ImportSource), PlatformSourceNames.RiotGames, StringComparison.OrdinalIgnoreCase);

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

            batch.Log($"PIPELINE EXE-COPYRIGHT exe='{exeName}' source={copyright.Source} years={years} text=\"{text}\"");
        }

        private static string TruncateForDebug(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength).TrimEnd() + "...";
        }
    }
}
