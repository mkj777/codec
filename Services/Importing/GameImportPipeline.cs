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
        private readonly HltbService _hltb;
        private readonly DisplayedAssetService _displayedAssets;

        public GameImportPipeline(
            GameNameService gameName,
            GameDetailsService gameDetails,
            SteamDetailsService steamDetails,
            RawgDetailsService rawgDetails,
            HltbService hltb,
            DisplayedAssetService displayedAssets)
        {
            _gameName = gameName;
            _gameDetails = gameDetails;
            _steamDetails = steamDetails;
            _rawgDetails = rawgDetails;
            _hltb = hltb;
            _displayedAssets = displayedAssets;
        }

        public async Task<GameImportResult> ImportAsync(GameImportRequest request, IReadOnlyCollection<Game> librarySnapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            {
                return GameImportResult.Invalid("No executable was selected.");
            }

            string normalizedExePath;
            try
            {
                normalizedExePath = Path.GetFullPath(request.ExecutablePath);
            }
            catch
            {
                return GameImportResult.Invalid("The selected executable path is invalid.");
            }

            if (!File.Exists(normalizedExePath))
            {
                return GameImportResult.Invalid("The selected executable no longer exists.");
            }

            if (GameContentHeuristics.PathMatchesUtility(normalizedExePath))
            {
                return GameImportResult.Invalid("Codec rejected this executable because it looks like a launcher or utility.");
            }

            if (librarySnapshot.Any(g => string.Equals(g.Executable, normalizedExePath, StringComparison.OrdinalIgnoreCase)))
            {
                return GameImportResult.Duplicate("This executable is already in your library.");
            }

            string folderLocation = string.IsNullOrWhiteSpace(request.FolderLocation)
                ? Path.GetDirectoryName(normalizedExePath) ?? string.Empty
                : request.FolderLocation;

            string detectedName = string.IsNullOrWhiteSpace(request.NameHint)
                ? _gameName.GetBestName(normalizedExePath) ?? Path.GetFileNameWithoutExtension(normalizedExePath)
                : request.NameHint.Trim();

            if (string.IsNullOrWhiteSpace(detectedName))
            {
                detectedName = Path.GetFileNameWithoutExtension(normalizedExePath);
            }

            if (GameContentHeuristics.NameMatchesUtility(detectedName))
            {
                return GameImportResult.Invalid($"Codec rejected '{detectedName}' because it looks like a launcher or utility.");
            }

            try
            {
                int? steamId = request.SteamAppId;
                int? rawgId = request.RawgId;

                if (!steamId.HasValue || !rawgId.HasValue)
                {
                    var resolvedIds = await _gameName.FindGameIdsAsync(normalizedExePath).ConfigureAwait(false);
                    steamId ??= resolvedIds.steamId;
                    rawgId ??= resolvedIds.rawgId;
                }

                if (!rawgId.HasValue && !string.IsNullOrWhiteSpace(detectedName))
                {
                    var mode = steamId.HasValue ? RawgValidationMode.SteamBacked : RawgValidationMode.Strict;
                    rawgId = await _gameDetails.ValidateGameAsync(detectedName, mode).ConfigureAwait(false);
                }

                if (steamId.HasValue && librarySnapshot.Any(g => g.SteamID == steamId.Value))
                {
                    return GameImportResult.Duplicate($"A game with Steam ID {steamId.Value} already exists in your library.");
                }

                if (rawgId.HasValue && librarySnapshot.Any(g => g.RawgID == rawgId.Value))
                {
                    return GameImportResult.Duplicate($"A game with RAWG ID {rawgId.Value} already exists in your library.");
                }

                var game = new Game
                {
                    Name = detectedName,
                    Executable = normalizedExePath,
                    FolderLocation = folderLocation,
                    ImportedFrom = request.ImportSource,
                    SteamID = steamId,
                    RawgID = rawgId
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
                    await _steamDetails.PopulateFromSteamAsync(game).ConfigureAwait(false);
                }

                if (game.RawgID.HasValue)
                {
                    await _rawgDetails.PopulateAsync(game).ConfigureAwait(false);
                }
                else
                {
                    await _rawgDetails.TryPopulateRawgFromSearchAsync(game).ConfigureAwait(false);
                }

                FinalizeFallbackLinks(game);

                await _hltb.PopulateAsync(game).ConfigureAwait(false);
                var displayedAssets = await _displayedAssets.EnsureDisplayedAssetsAsync(game).ConfigureAwait(false);
                ApplyDisplayedAssetHydration(game, displayedAssets);
                FinalizeFallbackLinks(game);

                if (!displayedAssets.AreRequiredAssetsReady)
                {
                    return GameImportResult.Failed($"Codec could not finish downloading the required artwork for {game.Name}.");
                }

                game.IsFullyImported = true;
                return GameImportResult.Added(game, $"{game.Name} was added to your library.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Import pipeline failed for '{normalizedExePath}': {ex.Message}");
                return GameImportResult.Failed("Codec could not finish importing this game.");
            }
        }

        private static void FinalizeFallbackLinks(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.RawgUrl))
            {
                if (!string.IsNullOrWhiteSpace(game.RawgSlug))
                {
                    game.RawgUrl = $"https://rawg.io/games/{game.RawgSlug}";
                }
                else if (game.RawgID.HasValue)
                {
                    game.RawgUrl = $"https://rawg.io/games/{game.RawgID.Value}";
                }
                else
                {
                    game.RawgUrl = "https://rawg.io";
                }
            }

            if (string.IsNullOrWhiteSpace(game.HltbUrl))
            {
                game.HltbUrl = "https://howlongtobeat.com";
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
            game.NotifyDisplayedAssetStateChanged();
        }
    }
}
