using Codec.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Fetching
{
    public sealed class DisplayedAssetService
    {
        public sealed record DisplayedAssetHydrationResult(
            int? GridDbId,
            string? CapsuleCachePath,
            bool HasHeroSource,
            string? HeroUrl,
            string? HeroCachePath,
            bool HasLogoSource,
            string? LogoUrl,
            string? LogoCachePath)
        {
            public bool IsCoverCached => HasLocalAsset(CapsuleCachePath);
            public bool IsHeroCached => HasLocalAsset(HeroCachePath);
            public bool IsLogoCached => HasLocalAsset(LogoCachePath);
            public bool AreRequiredAssetsReady => IsCoverCached && (!HasHeroSource || IsHeroCached) && (!HasLogoSource || IsLogoCached);

            private static bool HasLocalAsset(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                try
                {
                    if (Uri.TryCreate(path, UriKind.Absolute, out var parsed) && parsed.IsFile)
                    {
                        return System.IO.File.Exists(parsed.LocalPath);
                    }

                    return System.IO.File.Exists(path);
                }
                catch
                {
                    return false;
                }
            }
        }

        private readonly GameAssetService _gameAssets;
        private readonly GridDbService _gridDb;
        private readonly RawgDetailsService _rawgDetails;

        public DisplayedAssetService(GameAssetService gameAssets, GridDbService gridDb, RawgDetailsService rawgDetails)
        {
            _gameAssets = gameAssets;
            _gridDb = gridDb;
            _rawgDetails = rawgDetails;
        }

        public async Task<DisplayedAssetHydrationResult> EnsureDisplayedAssetsAsync(Game game, bool force = false, CancellationToken cancellationToken = default)
        {
            if (game == null)
            {
                return new DisplayedAssetHydrationResult(null, null, false, null, null, false, null, null);
            }

            var bundled = TryResolveBundledRiotAssets(game);
            if (bundled != null)
            {
                return bundled;
            }

            string? capsuleCachePath = game.LibraryCapsuleCache;
            int? gridDbId = game.GridDbId;
            int? steamMetadataId = game.EffectiveSteamMetadataAppId;

            if (game.SteamID.HasValue)
            {
                var coverPath = await _gameAssets.DownloadSteamLibraryCoverAsync(game.SteamID.Value, force, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    capsuleCachePath = coverPath;
                }
            }
            else
            {
                var gridResult = await _gridDb.ResolveGridAssetsAsync(game.EffectiveMetadataLookupName, game.GridDbId, game.LibraryCapsuleCache, forceCoverDownload: force, cancellationToken: cancellationToken).ConfigureAwait(false);
                gridDbId = gridResult.GridDbId;
                capsuleCachePath = gridResult.CoverCachePath;
            }

            bool hasLogoSource = !string.IsNullOrWhiteSpace(game.LibraryLogoUrl);
            string? logoCachePath = game.LibraryLogoCache;
            bool logoLookupFailed = false;
            if (!string.IsNullOrWhiteSpace(game.LibraryLogoUrl))
            {
                var logo = await _gameAssets.CacheImageWithOutcomeAsync("Logos", BuildAssetKey(game, "library_logo"), game.LibraryLogoUrl, force, cancellationToken).ConfigureAwait(false);
                logoLookupFailed = logo.Outcome == GameAssetService.AssetFetchOutcome.Failed;
                if (!string.IsNullOrWhiteSpace(logo.Path))
                {
                    logoCachePath = logo.Path;
                }
            }

            bool hasHeroSource = !string.IsNullOrWhiteSpace(game.LibraryHeroUrl);
            string? heroCachePath = game.LibraryHeroCache;
            bool needsSteamHeroFallback = false;
            bool preferSteamHeaderImage = false;

            if (!string.IsNullOrWhiteSpace(game.LibraryHeroUrl))
            {
                var hero = await _gameAssets.CacheImageWithOutcomeAsync("Heroes", BuildAssetKey(game, "library_hero"), game.LibraryHeroUrl, force, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(hero.Path))
                {
                    heroCachePath = hero.Path;
                }
                else if (steamMetadataId.HasValue &&
                         (hero.Outcome != GameAssetService.AssetFetchOutcome.Failed || !HasLocalFile(heroCachePath)))
                {
                    // A failed download keeps the hero already on disk; only a missing one needs the fallback.
                    needsSteamHeroFallback = true;
                }
            }
            else if (steamMetadataId.HasValue)
            {
                needsSteamHeroFallback = true;
            }

            // Logo 404 fallback for Steam games: clear broken logo and let hero fall back to Steam headers / RAWG.
            // A failed download is not a 404; the logo source stays so a later pass retries it.
            if (steamMetadataId.HasValue && hasLogoSource && logoCachePath == null && !logoLookupFailed)
            {
                needsSteamHeroFallback = true;
                preferSteamHeaderImage = true;
                hasLogoSource = false;
                logoCachePath = null;
            }

            if (steamMetadataId.HasValue && needsSteamHeroFallback)
            {
                string? steamHeaderUrl = null;

                if (preferSteamHeaderImage)
                {
                    steamHeaderUrl = await _gameAssets.FetchSteamHeaderImageUrlAsync(steamMetadataId.Value, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(steamHeaderUrl))
                {
                    steamHeaderUrl = await _gameAssets.ResolveSteamHeaderFallbackUrlAsync(steamMetadataId.Value, cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(steamHeaderUrl))
                {
                    var fallbackPath = await _gameAssets.CacheImageAsync("Heroes", BuildAssetKey(game, "header"), steamHeaderUrl, force, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fallbackPath))
                    {
                        hasHeroSource = true;
                        heroCachePath = fallbackPath;
                    }
                }

                if (string.IsNullOrWhiteSpace(heroCachePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _rawgDetails.TryPopulateRawgFromSearchAsync(game).ConfigureAwait(false);
                    if (game.RawgID.HasValue)
                    {
                        await _rawgDetails.PopulateAsync(game).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(game.LibraryHeroUrl))
                    {
                        var rawgHeroPath = await _gameAssets.CacheImageAsync("Heroes", BuildAssetKey(game, "rawg_hero"), game.LibraryHeroUrl, force, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(rawgHeroPath))
                        {
                            hasHeroSource = true;
                            heroCachePath = rawgHeroPath;
                        }
                    }
                }
            }

            string? epicLogo = TryResolveBundledEpicLogo(game);
            if (epicLogo != null)
            {
                hasLogoSource = true;
                logoCachePath = epicLogo;
            }

            return new DisplayedAssetHydrationResult(
                gridDbId,
                capsuleCachePath,
                hasHeroSource,
                game.LibraryHeroUrl,
                heroCachePath,
                hasLogoSource,
                game.LibraryLogoUrl,
                logoCachePath);
        }

        public DisplayedAssetHydrationResult Evaluate(Game game)
        {
            return new DisplayedAssetHydrationResult(
                game.GridDbId,
                game.LibraryCapsuleCache,
                game.HasHeroAssetSource || !string.IsNullOrWhiteSpace(game.LibraryHeroUrl),
                game.LibraryHeroUrl,
                game.LibraryHeroCache,
                game.HasLogoAssetSource || !string.IsNullOrWhiteSpace(game.LibraryLogoUrl),
                game.LibraryLogoUrl,
                game.LibraryLogoCache);
        }

        private static bool HasLocalFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return Uri.TryCreate(path, UriKind.Absolute, out var parsed) && parsed.IsFile
                    ? File.Exists(parsed.LocalPath)
                    : File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static readonly Dictionary<string, string> EpicBundledLogos = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Fortnite"] = "fortniteLogo.png",
        };

        private static string? TryResolveBundledEpicLogo(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name))
                return null;

            if (!EpicBundledLogos.TryGetValue(game.Name.Trim(), out var file))
                return null;

            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Epic", file);
            return File.Exists(path) ? path : null;
        }

        // Teamfight Tactics ships only its icon; it is not imported on its own yet.
        private sealed record BundledRiotAsset(string Folder, string? Capsule, string? Hero, string? Logo, string? Icon);

        private static readonly Dictionary<string, BundledRiotAsset> RiotBundledAssets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["2XKO"] = new("2XKO", "2xkoCapsule.jpg", "2xkoHero.png", "2xkoLogo.png", "2xkoIcon.ico"),
            ["League of Legends"] = new("LeagueOfLegends", "leagueCapsule.png", "leagueHero.jpg", "leagueLogo.png", "leagueIcon.ico"),
            ["Legends of Runeterra"] = new("LegendsOfRuneterra", "lorCapsule.png", "lorHero.jpg", "lorLogo.png", "lorIcon.ico"),
            ["LoR"] = new("LegendsOfRuneterra", "lorCapsule.png", "lorHero.jpg", "lorLogo.png", "lorIcon.ico"),
            ["VALORANT"] = new("Valorant", "valorantCapsule.png", "valorantHero.jpg", "valorantLogo.png", "valorantIcon.ico"),
            ["Valorant"] = new("Valorant", "valorantCapsule.png", "valorantHero.jpg", "valorantLogo.png", "valorantIcon.ico"),
            ["Teamfight Tactics"] = new("TeamfightTactics", null, null, null, "tftIcon.ico"),
        };

        /// <summary>The official icon Codec ships for a Riot game, or null when there is none on disk.</summary>
        public static string? ResolveBundledRiotIcon(Game game, string? appDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(game.Name) ||
                !RiotBundledAssets.TryGetValue(game.Name.Trim(), out var asset) ||
                asset.Icon == null)
                return null;

            string icon = Path.Combine(appDirectory ?? AppContext.BaseDirectory, "Assets", "Riot", asset.Folder, asset.Icon);
            return File.Exists(icon) ? icon : null;
        }

        private static DisplayedAssetHydrationResult? TryResolveBundledRiotAssets(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name))
                return null;

            if (!RiotBundledAssets.TryGetValue(game.Name.Trim(), out var asset) ||
                asset.Capsule == null || asset.Hero == null || asset.Logo == null)
                return null;

            string baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Riot", asset.Folder);
            string capsule = Path.Combine(baseDir, asset.Capsule);
            string hero = Path.Combine(baseDir, asset.Hero);
            string logo = Path.Combine(baseDir, asset.Logo);

            if (!File.Exists(capsule) || !File.Exists(hero) || !File.Exists(logo))
                return null;

            return new DisplayedAssetHydrationResult(
                GridDbId: null,
                CapsuleCachePath: capsule,
                HasHeroSource: true,
                HeroUrl: null,
                HeroCachePath: hero,
                HasLogoSource: true,
                LogoUrl: null,
                LogoCachePath: logo);
        }

        private static string BuildAssetKey(Game game, string suffix)
        {
            if (game.EffectiveSteamMetadataAppId.HasValue)
            {
                return $"steam_{game.EffectiveSteamMetadataAppId.Value}_{suffix}";
            }

            if (game.RawgID.HasValue)
            {
                return $"rawg_{game.RawgID.Value}_{suffix}";
            }

            string normalizedName = string.IsNullOrWhiteSpace(game.EffectiveMetadataLookupName)
                ? "game"
                : game.EffectiveMetadataLookupName.Replace(' ', '_').ToLowerInvariant();

            return $"{normalizedName}_{suffix}";
        }
    }
}
