using Codec.Models;
using Codec.Services.Scanning.Scanners;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Fetching
{
    /// <summary>
    /// Finds the small icon the rail shows beside a game's name and keeps it in the asset cache.
    /// </summary>
    /// <remarks>
    /// A source that answers "none" hands over to the next one. A source that could not be asked
    /// ends the search instead: whatever a later source found would be kept in its place and
    /// never looked at again. Nothing about a missing icon is stored, so a later pass asks again.
    /// </remarks>
    /// <summary>The part of the PICS client the icon search uses; tests stand in for Steam here.</summary>
    internal interface ISteamAssetSource
    {
        Task<(SteamLibraryAssets? Assets, bool LookupFailed)> LookupLibraryAssetsAsync(uint appId, CancellationToken ct = default);

        Task PrefetchLibraryAssetsAsync(IEnumerable<uint> appIds, CancellationToken ct = default);

        SteamLibraryAssets? TryGetCachedLibraryAssets(uint appId);
    }

    public sealed class GameIconService
    {
        public enum IconOutcome
        {
            Found,
            NotAvailable,
            Failed
        }

        public sealed record IconResult(IconOutcome Outcome, string? Path)
        {
            public static IconResult NotAvailable { get; } = new(IconOutcome.NotAvailable, null);
            public static IconResult Failed { get; } = new(IconOutcome.Failed, null);
            public static IconResult Found(string path) => new(IconOutcome.Found, path);
        }

        public sealed record IconUpdate(Guid GameId, string Path);

        private delegate Task<IconResult> IconSource(Game game, CancellationToken cancellationToken);

        private readonly GameAssetService _assets;
        private readonly ISteamAssetSource? _steam;
        private readonly IgdbService? _igdb;
        private readonly GridDbService? _gridDb;
        private readonly Func<string?> _steamRoot;
        private readonly string _appDirectory;
        private readonly string? _epicManifestsPath;

        public GameIconService(
            GameAssetService assets,
            SteamKitService? steamKit,
            IgdbService? igdb,
            GridDbService? gridDb,
            Func<string?>? steamRootProvider = null)
            : this(
                assets,
                steamKit,
                igdb,
                gridDb,
                steamRootProvider,
                appDirectory: null,
                epicManifestsPath: null)
        {
        }

        internal GameIconService(
            GameAssetService assets,
            ISteamAssetSource? steam,
            IgdbService? igdb,
            GridDbService? gridDb,
            Func<string?>? steamRootProvider,
            string? appDirectory,
            string? epicManifestsPath)
        {
            _assets = assets;
            _steam = steam;
            _igdb = igdb;
            _gridDb = gridDb;
            _steamRoot = steamRootProvider ?? (() => null);
            _appDirectory = appDirectory ?? AppContext.BaseDirectory;
            _epicManifestsPath = epicManifestsPath;
        }

        /// <summary>
        /// Where icons are looked for, best first. Moving a source is moving its line.
        /// </summary>
        private IReadOnlyList<IconSource> Chain => new IconSource[]
        {
            FromSteamAsync,
            FromLocalFilesAsync,
            FromIgdbAsync,
            // RAWG would sit here; it publishes no icons, so the rung is intentionally empty.
            FromSteamGridDbAsync,
        };

        public Task<IconResult> ResolveAsync(Game game, CancellationToken cancellationToken = default)
            => RunChainAsync(Chain.Select(source => (Func<CancellationToken, Task<IconResult>>)(ct => source(game, ct))), cancellationToken);

        internal static async Task<IconResult> RunChainAsync(IEnumerable<Func<CancellationToken, Task<IconResult>>> sources, CancellationToken cancellationToken)
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IconResult result;
                try
                {
                    result = await source(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Icons] A source threw: {ex.Message}");
                    result = IconResult.Failed;
                }

                if (result.Outcome == IconOutcome.Found && !string.IsNullOrWhiteSpace(result.Path))
                {
                    return result;
                }

                if (result.Outcome == IconOutcome.Failed)
                {
                    return IconResult.Failed;
                }
            }

            return IconResult.NotAvailable;
        }

        /// <summary>
        /// Gives each visible game that needs one an icon. Works on detached copies only and
        /// returns what changed; the caller writes it to the bound games and saves.
        /// </summary>
        public async Task<IReadOnlyList<IconUpdate>> BackfillAsync(
            IReadOnlyCollection<Game> detachedGames,
            int maxDegreeOfParallelism,
            Func<Task>? waitForTurn = null,
            CancellationToken cancellationToken = default)
        {
            // Hidden games wait for their icon until they are restored.
            List<Game> visible = detachedGames.Where(game => !game.IsHidden).ToList();
            if (visible.Count == 0)
            {
                return Array.Empty<IconUpdate>();
            }

            if (_steam != null)
            {
                uint[] steamIds = visible
                    .Select(GetSteamAppId)
                    .Where(id => id.HasValue)
                    .Select(id => (uint)id!.Value)
                    .ToArray();
                if (steamIds.Length > 0)
                {
                    await _steam.PrefetchLibraryAssetsAsync(steamIds, cancellationToken).ConfigureAwait(false);
                }
            }

            var updates = new ConcurrentQueue<IconUpdate>();
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism)
            };

            await Parallel.ForEachAsync(visible, options, async (game, ct) =>
            {
                if (waitForTurn != null)
                {
                    await waitForTurn().ConfigureAwait(false);
                }

                try
                {
                    if (!NeedsIcon(game))
                    {
                        return;
                    }

                    IconResult result = await ResolveAsync(game, ct).ConfigureAwait(false);
                    if (result.Outcome == IconOutcome.Found && result.Path != null)
                    {
                        updates.Enqueue(new IconUpdate(game.Id, result.Path));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Icons] Backfill for {game.Name} failed: {ex.Message}");
                }
            }).ConfigureAwait(false);

            return updates.ToList();
        }

        /// <summary>
        /// A game needs an icon when its file is missing, or when Steam lists an icon hash its
        /// file name does not carry. Only hashes already seen this session count, so the check
        /// itself never waits on Steam. Exe icons are only fetched again when the file is gone.
        /// </summary>
        public bool NeedsIcon(Game game)
        {
            if (!HasFile(game.LibraryIconCache))
            {
                return true;
            }

            int? appId = GetSteamAppId(game);
            SteamLibraryAssets? assets = appId.HasValue ? _steam?.TryGetCachedLibraryAssets((uint)appId.Value) : null;
            string[] hashes = new[] { assets?.ClientIconHash, assets?.IconHash }
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Select(hash => ShortHash(hash!))
                .ToArray();
            if (hashes.Length == 0)
            {
                return false;
            }

            string fileName = Path.GetFileName(game.LibraryIconCache!);
            return !hashes.Any(hash => fileName.Contains(hash, StringComparison.OrdinalIgnoreCase));
        }

        // 1. Steam, for any game with a Steam app id: the client .ico, then the 32 px jpg.
        private async Task<IconResult> FromSteamAsync(Game game, CancellationToken cancellationToken)
        {
            int? appId = GetSteamAppId(game);
            if (!appId.HasValue || _steam == null)
            {
                return IconResult.NotAvailable;
            }

            uint id = (uint)appId.Value;
            var (assets, lookupFailed) = await _steam.LookupLibraryAssetsAsync(id, cancellationToken).ConfigureAwait(false);
            if (assets == null)
            {
                return lookupFailed ? IconResult.Failed : IconResult.NotAvailable;
            }

            string? steamRoot = _steamRoot();
            var candidates = new (string? Hash, string Kind, string Extension, Func<string, string, string>? LocalPath)[]
            {
                (assets.ClientIconHash, "clienticon", "ico", (root, hash) => Path.Combine(root, "steam", "games", $"{hash}.ico")),
                (assets.IconHash, "icon", "jpg", (root, hash) => Path.Combine(root, "appcache", "librarycache", id.ToString(), $"{hash}.jpg")),
            };

            foreach (var (hash, kind, extension, localPath) in candidates)
            {
                if (string.IsNullOrWhiteSpace(hash))
                {
                    continue;
                }

                string key = $"steam_{id}_{kind}_{ShortHash(hash)}";
                var download = await _assets.CacheImageWithOutcomeAsync(
                    "Icons",
                    key,
                    SteamKitService.BuildIconUrl(id, hash, extension),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (download.Outcome == GameAssetService.AssetFetchOutcome.Cached && download.Path != null)
                {
                    return IconResult.Found(download.Path);
                }

                // The CDN does not have every icon, and it cannot be reached offline; the Steam
                // client often keeps the same file under the same hash.
                if (steamRoot != null && localPath != null)
                {
                    string? local = await CopyLocalFileAsync(localPath(steamRoot, hash), $"{key}.{extension}", cancellationToken).ConfigureAwait(false);
                    if (local != null)
                    {
                        return IconResult.Found(local);
                    }
                }

                if (download.Outcome == GameAssetService.AssetFetchOutcome.Failed)
                {
                    return IconResult.Failed;
                }
            }

            return IconResult.NotAvailable;
        }

        // 2. Official files on this machine: the icon Codec ships for Riot games, else the exe's own.
        private async Task<IconResult> FromLocalFilesAsync(Game game, CancellationToken cancellationToken)
        {
            string? bundled = DisplayedAssetService.ResolveBundledRiotIcon(game, _appDirectory);
            if (bundled != null)
            {
                return IconResult.Found(bundled);
            }

            string? executable = HasFile(game.Executable) ? game.Executable : null;
            if (executable == null && !string.IsNullOrWhiteSpace(game.EpicAppId))
            {
                executable = await EpicGamesScanner.FindLaunchExecutableAsync(game.EpicAppId, _epicManifestsPath, cancellationToken).ConfigureAwait(false);
            }

            if (executable == null)
            {
                return IconResult.NotAvailable;
            }

            byte[]? icon = await Task.Run(() => ExeIconReader.ReadIcon(executable), cancellationToken).ConfigureAwait(false);
            if (icon == null)
            {
                return IconResult.NotAvailable;
            }

            string fileName = $"exe_{HashText(executable.ToLowerInvariant())}.ico";
            string? stored = await _assets.StoreIconAsync(fileName, icon, cancellationToken).ConfigureAwait(false);
            return stored != null ? IconResult.Found(stored) : IconResult.Failed;
        }

        // 3. IGDB, which keeps icons as artworks of their own type. Few games have one.
        private async Task<IconResult> FromIgdbAsync(Game game, CancellationToken cancellationToken)
        {
            if (!game.IgdbId.HasValue || _igdb == null)
            {
                return IconResult.NotAvailable;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var lookup = await _igdb.FindIconUrlAsync(game.IgdbId.Value).ConfigureAwait(false);
            if (lookup.LookupFailed)
            {
                return IconResult.Failed;
            }

            return lookup.Url == null
                ? IconResult.NotAvailable
                : await DownloadAsync($"igdb_{game.IgdbId.Value}_icon", lookup.Url, cancellationToken).ConfigureAwait(false);
        }

        // 5. SteamGridDB, preferring an icon marked official.
        private async Task<IconResult> FromSteamGridDbAsync(Game game, CancellationToken cancellationToken)
        {
            if (_gridDb == null)
            {
                return IconResult.NotAvailable;
            }

            int? gridDbId = game.GridDbId ?? await _gridDb.FindGridDbIdAsync(game.EffectiveMetadataLookupName, cancellationToken).ConfigureAwait(false);
            if (!gridDbId.HasValue)
            {
                return IconResult.NotAvailable;
            }

            var lookup = await _gridDb.FindIconAsync(gridDbId.Value, cancellationToken).ConfigureAwait(false);
            if (lookup.LookupFailed)
            {
                return IconResult.Failed;
            }

            return lookup.Url == null
                ? IconResult.NotAvailable
                : await DownloadAsync($"griddb_{gridDbId.Value}_icon_{lookup.ImageId}", lookup.Url, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IconResult> DownloadAsync(string key, string url, CancellationToken cancellationToken)
        {
            var download = await _assets.CacheImageWithOutcomeAsync("Icons", key, url, cancellationToken: cancellationToken).ConfigureAwait(false);
            return download.Outcome switch
            {
                GameAssetService.AssetFetchOutcome.Cached when download.Path != null => IconResult.Found(download.Path),
                GameAssetService.AssetFetchOutcome.Failed => IconResult.Failed,
                _ => IconResult.NotAvailable
            };
        }

        private async Task<string?> CopyLocalFileAsync(string sourcePath, string fileName, CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(sourcePath))
                {
                    return null;
                }

                byte[] content = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                return await _assets.StoreIconAsync(fileName, content, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Icons] Copying '{sourcePath}' failed: {ex.Message}");
                return null;
            }
        }

        private static int? GetSteamAppId(Game game) => game.SteamID ?? game.SteamMetadataAppId;

        private static string ShortHash(string hash) => hash.Length > 12 ? hash[..12] : hash;

        private static string HashText(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

        private static bool HasFile(string? path)
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
    }
}
