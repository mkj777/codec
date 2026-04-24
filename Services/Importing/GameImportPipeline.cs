using Codec.Models;
using Codec.Services.Fetching;
using Codec.Services.Resolving;
using Codec.Services.Scanning;
using Codec.Services.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            Debug.WriteLine($"[PIPELINE] ENTRY name='{request.NameHint}' source='{request.ImportSource}' exe='{request.ExecutablePath}' lnk='{request.LaunchScriptPath}' steam={request.SteamAppId} rawg={request.RawgId}");
            cancellationToken.ThrowIfCancellationRequested();

            // Steam-sourced games launch via steam:// URI — exe is optional.
            bool isSteamSourced = request.SteamAppId.HasValue;

            string normalizedExePath = string.Empty;
            if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
            {
                try
                {
                    normalizedExePath = Path.GetFullPath(request.ExecutablePath);
                }
                catch
                {
                    if (!isSteamSourced)
                    {
                        Debug.WriteLine($"[PIPELINE] INVALID (bad path): '{request.ExecutablePath}'");
                        return GameImportResult.Invalid("The selected executable path is invalid.");
                    }
                    Debug.WriteLine($"[PIPELINE] WARN (steam bad exe path, ignoring): '{request.ExecutablePath}'");
                    normalizedExePath = string.Empty;
                }
            }
            else if (!isSteamSourced)
            {
                Debug.WriteLine($"[PIPELINE] INVALID (empty exe): '{request.NameHint}'");
                return GameImportResult.Invalid("No executable was selected.");
            }

            if (!string.IsNullOrEmpty(normalizedExePath) && !File.Exists(normalizedExePath))
            {
                if (!isSteamSourced)
                {
                    Debug.WriteLine($"[PIPELINE] INVALID (exe missing): '{normalizedExePath}'");
                    return GameImportResult.Invalid("The selected executable no longer exists.");
                }
                Debug.WriteLine($"[PIPELINE] WARN (steam exe missing, ignoring): '{normalizedExePath}'");
                normalizedExePath = string.Empty;
            }

            if (!string.IsNullOrEmpty(normalizedExePath) && GameContentHeuristics.PathMatchesUtility(normalizedExePath))
            {
                if (!isSteamSourced)
                {
                    Debug.WriteLine($"[PIPELINE] INVALID (utility path): '{normalizedExePath}'");
                    return GameImportResult.Invalid("Codec rejected this executable because it looks like a launcher or utility.");
                }
                Debug.WriteLine($"[PIPELINE] WARN (steam utility exe, ignoring): '{normalizedExePath}'");
                normalizedExePath = string.Empty;
            }

            if (!string.IsNullOrEmpty(normalizedExePath)
                && librarySnapshot.Any(g => string.Equals(g.Executable, normalizedExePath, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine($"[PIPELINE] DUPLICATE (exe in library): '{normalizedExePath}'");
                return GameImportResult.Duplicate("This executable is already in your library.");
            }

            string folderLocation = !string.IsNullOrWhiteSpace(request.FolderLocation)
                ? request.FolderLocation
                : (!string.IsNullOrEmpty(normalizedExePath) ? Path.GetDirectoryName(normalizedExePath) ?? string.Empty : string.Empty);

            string detectedName = string.IsNullOrWhiteSpace(request.NameHint)
                ? (!string.IsNullOrEmpty(normalizedExePath)
                    ? _gameName.GetBestName(normalizedExePath) ?? Path.GetFileNameWithoutExtension(normalizedExePath)
                    : string.Empty)
                : request.NameHint.Trim();

            if (string.IsNullOrWhiteSpace(detectedName) && !string.IsNullOrEmpty(normalizedExePath))
            {
                detectedName = Path.GetFileNameWithoutExtension(normalizedExePath);
            }

            // Normalize ASCII trademark notation to unicode for display/storage
            detectedName = Regex.Replace(detectedName, @"\(TM\)", "™", RegexOptions.IgnoreCase);
            detectedName = Regex.Replace(detectedName, @"\(R\)", "®", RegexOptions.IgnoreCase);

            if (GameContentHeuristics.NameMatchesUtility(detectedName))
            {
                Debug.WriteLine($"[PIPELINE] INVALID (utility name): '{detectedName}'");
                return GameImportResult.Invalid($"Codec rejected '{detectedName}' because it looks like a launcher or utility.");
            }

            try
            {
                int? steamId = request.SteamAppId;
                int? rawgId = request.RawgId;
                int? igdbId = request.IgdbId;

                bool isRiotSource = string.Equals(request.ImportSource, "Riot Games", StringComparison.OrdinalIgnoreCase);

                // Manual / un-resolved: try Steam first, then IGDB by name, RAWG as last resort
                if (!steamId.HasValue && !isRiotSource && !rawgId.HasValue && !igdbId.HasValue)
                {
                    var resolvedIds = await _gameName.FindGameIdsAsync(normalizedExePath).ConfigureAwait(false);
                    steamId ??= resolvedIds.steamId;
                    if (!string.IsNullOrWhiteSpace(resolvedIds.steamName))
                        detectedName = resolvedIds.steamName;
                }

                if (steamId.HasValue && librarySnapshot.Any(g => g.SteamID == steamId.Value))
                {
                    Debug.WriteLine($"[PIPELINE] DUPLICATE (steam id {steamId} in library): '{detectedName}'");
                    return GameImportResult.Duplicate($"A game with Steam ID {steamId.Value} already exists in your library.");
                }

                // Non-Steam path: IGDB name lookup first, RAWG fallback
                if (!steamId.HasValue && !igdbId.HasValue && !rawgId.HasValue && !string.IsNullOrWhiteSpace(detectedName))
                {
                    igdbId = await _igdb.FindIgdbIdByNameAsync(detectedName).ConfigureAwait(false);
                    if (!igdbId.HasValue)
                    {
                        rawgId = await _gameDetails.ValidateGameAsync(detectedName, RawgValidationMode.Strict).ConfigureAwait(false);
                    }
                }

                if (rawgId.HasValue && librarySnapshot.Any(g => g.RawgID == rawgId.Value))
                {
                    Debug.WriteLine($"[PIPELINE] DUPLICATE (rawg id {rawgId} in library): '{detectedName}'");
                    return GameImportResult.Duplicate($"A game with RAWG ID {rawgId.Value} already exists in your library.");
                }

                if (igdbId.HasValue && librarySnapshot.Any(g => g.IgdbId == igdbId.Value))
                {
                    Debug.WriteLine($"[PIPELINE] DUPLICATE (igdb id {igdbId} in library): '{detectedName}'");
                    return GameImportResult.Duplicate($"A game with IGDB ID {igdbId.Value} already exists in your library.");
                }

                var game = new Game
                {
                    Name = detectedName,
                    Executable = normalizedExePath,
                    FolderLocation = folderLocation,
                    ImportedFrom = request.ImportSource,
                    SteamID = steamId,
                    RawgID = rawgId,
                    IgdbId = igdbId,
                    LaunchScript = !string.IsNullOrWhiteSpace(request.LaunchScriptPath) && File.Exists(request.LaunchScriptPath)
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
                        Debug.WriteLine($"Folder size lookup failed for {game.Name}: {ex.Message}");
                    }
                }

                if (game.SteamID.HasValue)
                {
                    // Steam first (assets, release date), then HLTB (preferred TTB source), then IGDB (fills gaps)
                    await _steamDetails.PopulateFromSteamAsync(game).ConfigureAwait(false);
                    await _hltb.PopulateAsync(game).ConfigureAwait(false);

                    game.IgdbId = await _igdb.FindIgdbIdBySteamIdAsync(game.SteamID.Value).ConfigureAwait(false);
                    if (game.IgdbId.HasValue)
                    {
                        await _igdb.PopulateFromIgdbAsync(game).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Non-Steam: HLTB first (preferred TTB source), then IGDB/RAWG for metadata
                    await _hltb.PopulateAsync(game).ConfigureAwait(false);

                    if (game.IgdbId.HasValue)
                    {
                        await _igdb.PopulateFromIgdbAsync(game).ConfigureAwait(false);
                    }
                    else if (game.RawgID.HasValue)
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

                bool isFromPlatformScanner = !string.Equals(request.ImportSource, "Heuristic Scan", StringComparison.OrdinalIgnoreCase)
                    && !request.IsManual;

                if (!displayedAssets.AreRequiredAssetsReady && !isFromPlatformScanner)
                {
                    Debug.WriteLine($"[PIPELINE] FAILED (assets not ready, manual/heuristic): '{game.Name}' cover={displayedAssets.IsCoverCached} hero={displayedAssets.IsHeroCached}/{displayedAssets.HasHeroSource} logo={displayedAssets.IsLogoCached}/{displayedAssets.HasLogoSource}");
                    return GameImportResult.Failed($"Codec could not finish downloading the required artwork for {game.Name}.");
                }

                game.IsFullyImported = true;
                Debug.WriteLine($"[PIPELINE] ADDED: '{game.Name}' steam={game.SteamID} rawg={game.RawgID} lnk={game.LaunchScript}");
                return GameImportResult.Added(game, $"{game.Name} was added to your library.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PIPELINE] FAILED (exception) '{normalizedExePath}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
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
            game.LibCapsuleCache = hydration.CapsuleCachePath;
            game.HasHeroAssetSource = hydration.HasHeroSource;
            game.LibHeroUrl = hydration.HeroUrl;
            game.LibHeroCache = hydration.HeroCachePath;
            game.HasLogoAssetSource = hydration.HasLogoSource;
            game.LibLogoUrl = hydration.LogoUrl;
            game.LibLogoCache = hydration.LogoCachePath;
        }
    }
}
