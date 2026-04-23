using Codec.Models;
using System;
using System.Collections.Generic;
using System.IO;
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

        public DisplayedAssetService(GameAssetService gameAssets, GridDbService gridDb)
        {
            _gameAssets = gameAssets;
            _gridDb = gridDb;
        }

        public async Task<DisplayedAssetHydrationResult> EnsureDisplayedAssetsAsync(Game game, bool force = false)
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

            string? capsuleCachePath = game.LibCapsuleCache;
            int? gridDbId = game.GridDbId;

            if (game.SteamID.HasValue)
            {
                var coverPath = await _gameAssets.DownloadSteamLibraryCoverAsync(game.SteamID.Value, force).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    capsuleCachePath = coverPath;
                }
            }
            else
            {
                var gridResult = await _gridDb.ResolveGridAssetsAsync(game.Name, game.GridDbId, game.LibCapsuleCache, forceCoverDownload: force).ConfigureAwait(false);
                gridDbId = gridResult.GridDbId;
                capsuleCachePath = gridResult.CoverCachePath;
            }

            bool hasHeroSource = !string.IsNullOrWhiteSpace(game.LibHeroUrl);
            string? heroCachePath = game.LibHeroCache;
            if (!string.IsNullOrWhiteSpace(game.LibHeroUrl))
            {
                var heroPath = await _gameAssets.CacheImageAsync("Heroes", BuildAssetKey(game, "hero"), game.LibHeroUrl, force).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(heroPath))
                {
                    heroCachePath = heroPath;
                }
            }

            bool hasLogoSource = !string.IsNullOrWhiteSpace(game.LibLogoUrl);
            string? logoCachePath = game.LibLogoCache;
            if (!string.IsNullOrWhiteSpace(game.LibLogoUrl))
            {
                var logoPath = await _gameAssets.CacheImageAsync("Logos", BuildAssetKey(game, "logo"), game.LibLogoUrl, force).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(logoPath))
                {
                    logoCachePath = logoPath;
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
                game.LibHeroUrl,
                heroCachePath,
                hasLogoSource,
                game.LibLogoUrl,
                logoCachePath);
        }

        public DisplayedAssetHydrationResult Evaluate(Game game)
        {
            return new DisplayedAssetHydrationResult(
                game.GridDbId,
                game.LibCapsuleCache,
                game.HasHeroAssetSource || !string.IsNullOrWhiteSpace(game.LibHeroUrl),
                game.LibHeroUrl,
                game.LibHeroCache,
                game.HasLogoAssetSource || !string.IsNullOrWhiteSpace(game.LibLogoUrl),
                game.LibLogoUrl,
                game.LibLogoCache);
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

        private sealed record BundledRiotAsset(string Folder, string Capsule, string Hero, string Logo);

        private static readonly Dictionary<string, BundledRiotAsset> RiotBundledAssets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["2XKO"] = new("2XKO", "2xkoCapsule.jpg", "2xkoHero.png", "2xkoLogo.png"),
            ["League of Legends"] = new("LeagueOfLegends", "leagueCapsule.png", "leagueHero.jpg", "leagueLogo.png"),
            ["VALORANT"] = new("Valorant", "valorantCapsule.png", "valorantHero.jpg", "valorantLogo.png"),
            ["Valorant"] = new("Valorant", "valorantCapsule.png", "valorantHero.jpg", "valorantLogo.png"),
        };

        private static DisplayedAssetHydrationResult? TryResolveBundledRiotAssets(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name))
                return null;

            if (!RiotBundledAssets.TryGetValue(game.Name.Trim(), out var asset))
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
            if (game.SteamID.HasValue)
            {
                return $"steam_{game.SteamID.Value}_{suffix}";
            }

            if (game.RawgID.HasValue)
            {
                return $"rawg_{game.RawgID.Value}_{suffix}";
            }

            string normalizedName = string.IsNullOrWhiteSpace(game.Name)
                ? "game"
                : game.Name.Replace(' ', '_').ToLowerInvariant();

            return $"{normalizedName}_{suffix}";
        }
    }
}
