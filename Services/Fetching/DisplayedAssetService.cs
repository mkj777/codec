using Codec.Models;
using System;
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
            public bool AreRequiredAssetsReady => IsCoverCached && IsHeroCached && (!HasLogoSource || IsLogoCached);

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
