using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codec.Models;
using Codec.Services;

namespace Codec.Services.Fetching
{
    public class IgdbService
    {
        private static readonly Regex TrademarkRegex = new(@"\(TM\)|\(R\)|™|®|©", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string ProxyBase = "https://codec-api-proxy.vercel.app/api/igdb";
        private const string ExternalGamesEndpoint = ProxyBase + "/external_games";
        private const string GamesEndpoint = ProxyBase + "/games";
        private const string CollectionsEndpoint = ProxyBase + "/collections";
        private const string TimeToBeatsEndpoint = ProxyBase + "/game_time_to_beats";
        private const string ArtworksEndpoint = ProxyBase + "/artworks";

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<int, int> _steamIdByIgdbIdCache = new();

        public IgdbService()
            : this(new HttpClient())
        {
        }

        public IgdbService(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<int?> FindIgdbIdByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Debug.WriteLine("[IGDB] FindIgdbIdByNameAsync: empty name, skipping");
                return null;
            }

            name = NormalizeIgdbSearchName(name);
            Debug.WriteLine($"[IGDB] FindIgdbIdByNameAsync: searching for '{name}'");

            try
            {
                string escaped = name.Replace("\"", "\\\"");
                string body = $"search \"{escaped}\"; fields id, name; limit 1;";
                string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine($"[IGDB] FindIgdbIdByNameAsync: empty response for '{name}'");
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FindIgdbIdByNameAsync: non-array response for '{name}': {json}");
                    return null;
                }

                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FindIgdbIdByNameAsync: no results for '{name}'");
                    return null;
                }

                int? id = TryGetInt(first, "id");
                string? foundName = GetString(first, "name");
                Debug.WriteLine($"[IGDB] FindIgdbIdByNameAsync: found id={id} name='{foundName}' for query '{name}'");
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FindIgdbIdByNameAsync: EXCEPTION for '{name}': {ex.Message}");
                return null;
            }
        }

        public Task<(int? Id, int? ReleaseYear)> FindIgdbMatchByNameAsync(string name)
            => FindIgdbMatchByNameAsync(name, allowedReleaseYears: null);

        public async Task<(int? Id, int? ReleaseYear)> FindIgdbMatchByNameAsync(string name, IReadOnlySet<int>? allowedReleaseYears)
        {
            if (string.IsNullOrWhiteSpace(name))
                return (null, null);

            name = NormalizeIgdbSearchName(name);
            Debug.WriteLine($"[IGDB] FindIgdbMatchByNameAsync: searching for '{name}'");

            try
            {
                var candidates = await SearchAndParseCandidatesAsync(name, name, allowedReleaseYears).ConfigureAwait(false);

                int bestPrimaryScore = candidates.Count > 0 ? candidates.Max(c => c.NameScore) : 0;
                if (bestPrimaryScore < 60)
                {
                    string stripped = StripRemasterSuffix(name);
                    if (!string.IsNullOrWhiteSpace(stripped) &&
                        !stripped.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[IGDB] FindIgdbMatchByNameAsync: weak primary (score={bestPrimaryScore}); retry with stripped='{stripped}'");
                        var fallback = await SearchAndParseCandidatesAsync(stripped, name, allowedReleaseYears).ConfigureAwait(false);
                        int bestFallbackScore = fallback.Count > 0 ? fallback.Max(c => c.NameScore) : 0;
                        if (bestFallbackScore > bestPrimaryScore)
                        {
                            candidates = fallback;
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    Debug.WriteLine($"[IGDB] FindIgdbMatchByNameAsync: no acceptable release-year match for '{name}' allowed={FormatYears(allowedReleaseYears)}");
                    return (null, null);
                }

                IReadOnlyDictionary<int, (int SteamId, string? SteamName)> steamLookup =
                    await FindSteamIdsByIgdbIdsAsync(candidates.Select(c => c.Id)).ConfigureAwait(false);
                candidates = candidates
                    .Select(c => steamLookup.TryGetValue(c.Id, out var info)
                        ? c with { SteamId = info.SteamId, SteamName = info.SteamName }
                        : c)
                    .ToList();

                IgdbNameCandidate best = candidates
                    .OrderByDescending(c => c.NameScore)
                    .ThenByDescending(c => GameTypeRank(c.GameType))
                    .ThenBy(c => SteamNameContainsYear(c.SteamName))
                    .ThenByDescending(c => c.ReleaseYear ?? 0)
                    .ThenByDescending(c => c.SteamId.HasValue)
                    .ThenBy(c => c.SearchOrder)
                    .First();

                Debug.WriteLine($"[IGDB] FindIgdbMatchByNameAsync: id={best.Id} name='{best.Name}' year={best.ReleaseYear?.ToString() ?? "?"} type='{best.GameType ?? "-"}' steam={best.SteamId?.ToString() ?? "-"} steamName='{best.SteamName ?? "-"}' score={best.NameScore} for '{name}'");
                return (best.Id, best.ReleaseYear);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FindIgdbMatchByNameAsync: EXCEPTION for '{name}': {ex.Message}");
                throw;
            }
        }

        private async Task<List<IgdbNameCandidate>> SearchAndParseCandidatesAsync(
            string queryName,
            string scoringName,
            IReadOnlySet<int>? allowedReleaseYears)
        {
            string escaped = queryName.Replace("\"", "\\\"");
            string body = $"search \"{escaped}\"; fields id, name, first_release_date, release_dates.date, game_type.type; limit 10;";
            string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);

            var candidates = new List<IgdbNameCandidate>();
            if (string.IsNullOrWhiteSpace(json))
                return candidates;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return candidates;

            int searchOrder = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int? id = TryGetInt(item, "id");
                if (!id.HasValue)
                {
                    continue;
                }

                string? foundName = GetString(item, "name");
                int? releaseYear = null;
                if (item.TryGetProperty("first_release_date", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number)
                    releaseYear = DateTimeOffset.FromUnixTimeSeconds(tsProp.GetInt64()).Year;

                var candidateYears = new HashSet<int>();
                if (releaseYear.HasValue) candidateYears.Add(releaseYear.Value);
                if (item.TryGetProperty("release_dates", out var rdArr) && rdArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rd in rdArr.EnumerateArray())
                    {
                        if (rd.ValueKind == JsonValueKind.Object &&
                            rd.TryGetProperty("date", out var dProp) &&
                            dProp.ValueKind == JsonValueKind.Number &&
                            dProp.TryGetInt64(out long dSecs) && dSecs > 0)
                        {
                            candidateYears.Add(DateTimeOffset.FromUnixTimeSeconds(dSecs).Year);
                        }
                    }
                }

                string? gameType = null;
                if (item.TryGetProperty("game_type", out var gtProp) && gtProp.ValueKind == JsonValueKind.Object)
                {
                    gameType = NormalizeGameType(GetString(gtProp, "type"));
                }

                if (!ReleaseYearMatches(allowedReleaseYears, candidateYears))
                {
                    Debug.WriteLine($"[IGDB] SearchAndParseCandidatesAsync: rejected id={id} name='{foundName}' years={FormatYears(candidateYears)} allowed={FormatYears(allowedReleaseYears)} for query '{queryName}'");
                    continue;
                }

                candidates.Add(new IgdbNameCandidate(
                    id.Value,
                    foundName ?? string.Empty,
                    releaseYear,
                    CalculateIgdbNameScore(scoringName, foundName),
                    searchOrder++,
                    SteamId: null,
                    SteamName: null,
                    GameType: gameType));
            }

            return candidates;
        }

        private static readonly Regex SteamNameYearRegex =
            new(@"\b(19[7-9]\d|20\d{2})\b", RegexOptions.Compiled);

        private static bool SteamNameContainsYear(string? steamName)
            => !string.IsNullOrWhiteSpace(steamName) && SteamNameYearRegex.IsMatch(steamName);

        private sealed record IgdbNameCandidate(
            int Id,
            string Name,
            int? ReleaseYear,
            int NameScore,
            int SearchOrder,
            int? SteamId,
            string? SteamName,
            string? GameType);

        private static int GameTypeRank(string? gameType) => NormalizeGameType(gameType) switch
        {
            "main_game" or "remake" or "remaster" => 3,
            "port" or "expanded_game" or "standalone_expansion" => 2,
            "bundle" or "pack" or "dlc_addon" or "expansion" or "mod"
                or "episode" or "season" or "update" or "fork" => 1,
            _ => 0
        };

        private static string? NormalizeGameType(string? gameType)
        {
            if (string.IsNullOrWhiteSpace(gameType))
            {
                return null;
            }

            string normalized = Regex.Replace(gameType.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            return normalized switch
            {
                "dlc_add_on" => "dlc_addon",
                _ => normalized
            };
        }

        public async Task<int?> FindIgdbIdBySteamIdAsync(int steamId)
        {
            Debug.WriteLine($"[IGDB] FindIgdbIdBySteamIdAsync: looking up steam={steamId}");
            try
            {
                string body = $"fields name, game; where uid = \"{steamId}\" & external_game_source = 1; limit 1;";
                string json = await PostAsync(ExternalGamesEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine($"[IGDB] FindIgdbIdBySteamIdAsync: empty response for steam={steamId}");
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FindIgdbIdBySteamIdAsync: non-array response for steam={steamId}: {json}");
                    return null;
                }

                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FindIgdbIdBySteamIdAsync: no match for steam={steamId}");
                    return null;
                }

                int? igdbId = TryGetInt(first, "game");
                Debug.WriteLine($"[IGDB] FindIgdbIdBySteamIdAsync: steam={steamId} → igdb={igdbId}");
                return igdbId;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FindIgdbIdBySteamIdAsync: EXCEPTION for steam={steamId}: {ex.Message}");
                return null;
            }
        }

        public async Task<int?> FindSteamIdByIgdbIdAsync(int igdbId)
        {
            if (igdbId <= 0)
            {
                return null;
            }

            if (_steamIdByIgdbIdCache.TryGetValue(igdbId, out int cachedSteamId))
            {
                Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: cache igdb={igdbId} -> steam={cachedSteamId}");
                return cachedSteamId;
            }

            Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: looking up igdb={igdbId}");
            try
            {
                string body = $"fields game, name, uid; where game = {igdbId} & external_game_source = 1; limit 10;";
                string json = await PostAsync(ExternalGamesEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: empty response for igdb={igdbId}");
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: non-array response for igdb={igdbId}: {json}");
                    return null;
                }

                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    int? steamId = TryGetSteamUid(entry);

                    if (steamId.HasValue && steamId.Value > 0)
                    {
                        string? name = GetString(entry, "name");
                        _steamIdByIgdbIdCache[igdbId] = steamId.Value;
                        Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: igdb={igdbId} -> steam={steamId} name='{name}'");
                        return steamId;
                    }
                }

                Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: no steam external game for igdb={igdbId}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FindSteamIdByIgdbIdAsync: EXCEPTION for igdb={igdbId}: {ex.Message}");
                return null;
            }
        }

        private async Task<IReadOnlyDictionary<int, (int SteamId, string? SteamName)>> FindSteamIdsByIgdbIdsAsync(IEnumerable<int> igdbIds)
        {
            int[] ids = igdbIds
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            var results = new Dictionary<int, (int SteamId, string? SteamName)>();
            var missing = new List<int>();
            foreach (int id in ids)
            {
                if (_steamIdByIgdbIdCache.TryGetValue(id, out int cachedSteamId))
                {
                    results[id] = (cachedSteamId, null);
                }
                else
                {
                    missing.Add(id);
                }
            }

            if (missing.Count == 0)
            {
                return results;
            }

            try
            {
                string idPredicate = missing.Count == 1
                    ? $"game = {missing[0]}"
                    : $"game = ({string.Join(", ", missing)})";
                int limit = Math.Max(10, missing.Count * 2);
                string body = $"fields game, name, uid; where {idPredicate} & external_game_source = 1; limit {limit};";
                string json = await PostAsync(ExternalGamesEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return results;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FindSteamIdsByIgdbIdsAsync: non-array response: {json}");
                    return results;
                }

                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    int? gameId = TryGetInt(entry, "game");
                    int? steamId = TryGetSteamUid(entry);
                    string? steamName = GetString(entry, "name");
                    if (gameId.HasValue && steamId.HasValue && gameId.Value > 0 && steamId.Value > 0)
                    {
                        results[gameId.Value] = (steamId.Value, steamName);
                        _steamIdByIgdbIdCache[gameId.Value] = steamId.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FindSteamIdsByIgdbIdsAsync: EXCEPTION: {ex.Message}");
            }

            return results;
        }

        private static int? TryGetSteamUid(JsonElement entry)
        {
            string? uid = GetString(entry, "uid");
            return int.TryParse(uid, out int parsedUid)
                ? parsedUid
                : TryGetInt(entry, "uid");
        }

        private static int CalculateIgdbNameScore(string query, string? candidateName)
        {
            string normalizedQuery = NormalizeIgdbLookupName(query);
            string normalizedCandidate = NormalizeIgdbLookupName(candidateName);
            if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                return 0;
            }

            if (string.Equals(normalizedQuery, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            var queryTokens = TokenizeIgdbName(normalizedQuery);
            var candidateTokens = TokenizeIgdbName(normalizedCandidate);
            if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return 0;
            }

            int overlap = queryTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
            if (overlap == 0)
            {
                return 0;
            }

            if (overlap == queryTokens.Count)
            {
                int extraTokens = Math.Max(0, candidateTokens.Count - queryTokens.Count);
                return Math.Max(60, 85 - extraTokens * 5);
            }

            int union = queryTokens.Union(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
            return union == 0 ? 0 : (int)Math.Round(overlap / (double)union * 60);
        }

        private static string NormalizeIgdbLookupName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string normalized = GameNameCleaner.RemoveTrailingDomainTag(name);
            normalized = TrademarkRegex.Replace(normalized, " ");
            normalized = Regex.Replace(normalized, @"(?<=[a-z])(?=[A-Z])", " ");
            normalized = Regex.Replace(normalized, @"\b(tm|r)\b", " ", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"[^A-Za-z0-9]+", " ");
            return Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
        }

        private static string NormalizeIgdbSearchName(string name)
        {
            string normalized = GameNameCleaner.RemoveTrailingDomainTag(name);
            normalized = TrademarkRegex.Replace(normalized, " ");
            normalized = Regex.Replace(normalized, @"(?<=[a-z])(?=[A-Z])", " ");
            normalized = Regex.Replace(normalized, @"\b(tm|r)\b", " ", RegexOptions.IgnoreCase);
            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        private static IReadOnlySet<string> TokenizeIgdbName(string normalizedName)
            => normalizedName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public async Task PopulateFromIgdbAsync(Game game)
        {
            if (game == null || !game.IgdbId.HasValue)
            {
                Debug.WriteLine("[IGDB] PopulateFromIgdbAsync: skipped — game null or no IgdbId");
                return;
            }

            int igdbId = game.IgdbId.Value;
            Debug.WriteLine($"[IGDB] ── PopulateFromIgdbAsync START igdbId={igdbId} name='{game.Name}' ──");

            var gameTask = FetchGameAsync(igdbId);
            var timeTask = FetchTimeToBeatAsync(igdbId);

            await Task.WhenAll(gameTask, timeTask).ConfigureAwait(false);

            Debug.WriteLine($"[IGDB] ApplyGameDetails igdbId={igdbId}");
            ApplyGameDetails(game, gameTask.Result);
            Debug.WriteLine($"[IGDB] After ApplyGameDetails: releaseDate={game.ReleaseDate:yyyy-MM-dd} franchise='{game.FranchiseName}' franchiseId={game.IgdbFranchiseId}");

            Debug.WriteLine($"[IGDB] ApplyVersionMetadata igdbId={igdbId}");
            await ApplyVersionMetadataAsync(game, gameTask.Result).ConfigureAwait(false);
            Debug.WriteLine($"[IGDB] After ApplyVersionMetadata: category='{game.IgdbCategoryName}' isRemake={game.IsRemakeOrRemaster} parentId={game.IgdbVersionParentId} originalName='{game.OriginalGameName}' originalDate={game.OriginalReleaseDate:yyyy-MM-dd}");

            ApplyTimeToBeat(game, timeTask.Result);

            if (game.IgdbFranchiseId.HasValue)
            {
                Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGames franchiseId={game.IgdbFranchiseId.Value}");
                await FetchAndStoreFranchiseGamesAsync(game, game.IgdbFranchiseId.Value).ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] After FetchFranchiseGames: count={game.FranchiseGames?.Count ?? 0}");
            }
            else
            {
                Debug.WriteLine($"[IGDB] No franchiseId on game — skipping franchise games fetch");
            }

            // Artworks: only if Steam didn't provide a hero (non-Steam path)
            if (!game.SteamID.HasValue && (string.IsNullOrWhiteSpace(game.LibraryHeroUrl) || IsPlaceholder(game.LibraryHeroUrl)))
            {
                string? artworkUrl = await FetchFirstArtworkAsync(igdbId).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    game.LibraryHeroUrl = artworkUrl;
                    Debug.WriteLine($"[IGDB] Artwork set: {artworkUrl}");
                }
                else
                {
                    Debug.WriteLine($"[IGDB] No artwork found for igdbId={igdbId}");
                }
            }

            Debug.WriteLine($"[IGDB] ── PopulateFromIgdbAsync END igdbId={igdbId} ──");
        }

        private async Task<JsonElement?> FetchGameAsync(int igdbId)
        {
            Debug.WriteLine($"[IGDB] FetchGameAsync: requesting igdbId={igdbId}");
            try
            {
                string body = $@"fields
  name,
  slug,
  summary,
  storyline,
  first_release_date,
  game_type.type,
  parent_game,
  version_parent.id,
  version_parent.name,
  version_parent.first_release_date,
  version_parent.collections.name,
  release_dates.date,
  url,
  cover.image_id,
  artworks.image_id,
  screenshots.image_id,
  videos.video_id,
  videos.name,
  genres.name,
  platforms.name,
  collections.id,
  collections.name,
  themes.name,
  involved_companies.company.name,
  involved_companies.developer,
  involved_companies.publisher,
  age_ratings.rating,
  age_ratings.category,
  age_ratings.rating_cover_url,
  websites.url,
  websites.category,
  aggregated_rating,
  aggregated_rating_count,
  total_rating,
  total_rating_count;
where id = {igdbId};
limit 1;";
                string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] FetchGameAsync raw response igdbId={igdbId}: {json}");

                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine($"[IGDB] FetchGameAsync: empty response for igdbId={igdbId}");
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FetchGameAsync: non-array response for igdbId={igdbId}");
                    return null;
                }

                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FetchGameAsync: no game found for igdbId={igdbId}");
                    return null;
                }

                Debug.WriteLine($"[IGDB] FetchGameAsync: got game '{GetString(first, "name")}' for igdbId={igdbId}");
                return first.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FetchGameAsync: EXCEPTION for igdbId={igdbId}: {ex.Message}");
                return null;
            }
        }

        private async Task<JsonElement?> FetchTimeToBeatAsync(int igdbId)
        {
            Debug.WriteLine($"[IGDB] FetchTimeToBeatAsync: requesting igdbId={igdbId}");
            try
            {
                string body = $"fields completely,count,game_id,hastily,normally; where game_id = {igdbId}; limit 1;";
                string json = await PostAsync(TimeToBeatsEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine($"[IGDB] FetchTimeToBeatAsync: empty response for igdbId={igdbId}");
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FetchTimeToBeatAsync: non-array for igdbId={igdbId}: {json}");
                    return null;
                }

                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FetchTimeToBeatAsync: no data for igdbId={igdbId}");
                    return null;
                }

                Debug.WriteLine($"[IGDB] FetchTimeToBeatAsync: got data for igdbId={igdbId}");
                return first.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FetchTimeToBeatAsync: EXCEPTION for igdbId={igdbId}: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> FetchFirstArtworkAsync(int igdbId)
        {
            try
            {
                string body = $"fields image_id, game; where game = {igdbId}; limit 1;";
                string json = await PostAsync(ArtworksEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                string? imageId = GetString(first, "image_id");
                if (string.IsNullOrWhiteSpace(imageId))
                {
                    return null;
                }

                return BuildImageUrl(imageId, "t_1080p");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IGDB artworks fetch failed for id={igdbId}: {ex.Message}");
                return null;
            }
        }

        private void ApplyGameDetails(Game game, JsonElement? element)
        {
            if (element is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Slug + URL
            string? slug = GetString(root, "slug");
            string? igdbUrl = GetString(root, "url");

            if (!string.IsNullOrWhiteSpace(igdbUrl))
            {
                game.IgdbUrl = igdbUrl;
            }
            else if (!string.IsNullOrWhiteSpace(slug))
            {
                game.IgdbUrl = $"https://www.igdb.com/games/{slug}";
            }

            // Release date: earliest across all platform dates + first_release_date
            {
                long? earliest = null;

                if (root.TryGetProperty("first_release_date", out var frdNode) && frdNode.TryGetInt64(out long frdSecs))
                    earliest = frdSecs;

                if (root.TryGetProperty("release_dates", out var rdArray) && rdArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rd in rdArray.EnumerateArray())
                    {
                        if (rd.TryGetProperty("date", out var dateNode) && dateNode.TryGetInt64(out long dateSecs) && dateSecs > 0)
                        {
                            if (earliest == null || dateSecs < earliest)
                                earliest = dateSecs;
                        }
                    }
                }

                if (earliest.HasValue)
                {
                    game.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(earliest.Value).UtcDateTime;
                }
            }

            // Summary → Description (Steam owns this if already set)
            string? summary = GetString(root, "summary") ?? GetString(root, "storyline");
            if (!string.IsNullOrWhiteSpace(summary) && ShouldOverwrite(game.Description, game.SteamID))
            {
                game.Description = TruncateWithEllipsis(summary, 250);
            }

            // Genres
            var genres = GetNameList(root, "genres");
            if (genres.Count > 0 && (game.Genres == null || game.Genres.Count == 0 || !game.SteamID.HasValue))
            {
                game.Genres = genres;
            }

            // Platforms
            var platforms = GetNameList(root, "platforms");
            if (platforms.Count > 0 && (game.Platforms == null || game.Platforms.Count == 0 || !game.SteamID.HasValue))
            {
                game.Platforms = platforms;
            }

            // Collection (first entry wins) — stored on the same FranchiseName/IgdbFranchiseId fields
            if (root.TryGetProperty("collections", out var collectionsNode) && collectionsNode.ValueKind == JsonValueKind.Array)
            {
                var firstCollection = collectionsNode.EnumerateArray().FirstOrDefault();
                if (firstCollection.ValueKind == JsonValueKind.Object)
                {
                    game.FranchiseName = GetString(firstCollection, "name");
                    game.IgdbFranchiseId = TryGetInt(firstCollection, "id");
                }
            }

            // Developer / Publisher
            if (root.TryGetProperty("involved_companies", out var companies) && companies.ValueKind == JsonValueKind.Array)
            {
                string? dev = null;
                string? pub = null;
                foreach (var c in companies.EnumerateArray())
                {
                    string? cname = c.TryGetProperty("company", out var co) && co.ValueKind == JsonValueKind.Object
                        ? GetString(co, "name")
                        : null;
                    if (string.IsNullOrWhiteSpace(cname)) continue;

                    bool isDev = c.TryGetProperty("developer", out var dp) && dp.ValueKind == JsonValueKind.True;
                    bool isPub = c.TryGetProperty("publisher", out var pp) && pp.ValueKind == JsonValueKind.True;

                    if (isDev && dev == null) dev = cname;
                    if (isPub && pub == null) pub = cname;
                }

                if (!string.IsNullOrWhiteSpace(dev) && ShouldOverwrite(game.Developer, game.SteamID))
                {
                    game.Developer = dev;
                }
                if (!string.IsNullOrWhiteSpace(pub) && ShouldOverwrite(game.Publisher, game.SteamID))
                {
                    game.Publisher = pub;
                }
            }

            // Media: videos (YouTube) + screenshots — only if Steam didn't supply any
            if (game.Media == null || game.Media.Count == 0)
            {
                var media = new List<string>();
                if (root.TryGetProperty("videos", out var videosNode) && videosNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in videosNode.EnumerateArray())
                    {
                        string? vid = GetString(v, "video_id");
                        if (!string.IsNullOrWhiteSpace(vid))
                        {
                            media.Add($"https://www.youtube.com/watch?v={vid}");
                        }
                    }
                }
                if (root.TryGetProperty("screenshots", out var shotsNode) && shotsNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in shotsNode.EnumerateArray())
                    {
                        string? imageId = GetString(s, "image_id");
                        if (!string.IsNullOrWhiteSpace(imageId))
                        {
                            media.Add(BuildImageUrl(imageId, "t_1080p"));
                        }
                    }
                }
                if (media.Count > 0)
                {
                    game.Media = media;
                }
            }
        }

        private static void ApplyTimeToBeat(Game game, JsonElement? element)
        {
            if (element is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            int? normally = TryGetInt(root, "normally");
            int? completely = TryGetInt(root, "completely");

            // Only fill in values not already provided by HLTB
            if (!game.TimeToCompleteMainStory.HasValue && normally.HasValue && normally.Value > 0)
            {
                game.TimeToCompleteMainStory = normally.Value;
            }
            if (!game.TimeToCompleteCompletionist.HasValue && completely.HasValue && completely.Value > 0)
            {
                game.TimeToCompleteCompletionist = completely.Value;
            }
        }

        private async Task ApplyVersionMetadataAsync(Game game, JsonElement? element)
        {
            if (element is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            {
                Debug.WriteLine("[IGDB] ApplyVersionMetadataAsync: no element, skipping");
                return;
            }

            // game_type is a relation object: { "type": "Remaster" }.
            string? gameTypeStr = null;
            if (root.TryGetProperty("game_type", out var gameTypeNode) && gameTypeNode.ValueKind == JsonValueKind.Object)
            {
                gameTypeStr = NormalizeGameType(GetString(gameTypeNode, "type"));
                Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: game_type.type='{gameTypeStr}'");
            }
            else
            {
                Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: game_type absent or not object (kind={gameTypeNode.ValueKind})");
            }

            game.IgdbCategory = MapGameTypeToCategory(gameTypeStr);
            game.IgdbCategoryName = FormatGameTypeName(gameTypeStr);

            bool isRemakeOrRemaster = gameTypeStr == "remake" || gameTypeStr == "remaster";
            Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: isRemakeOrRemaster={isRemakeOrRemaster} category={game.IgdbCategory} categoryName='{game.IgdbCategoryName}'");

            if (!isRemakeOrRemaster)
            {
                game.OriginalReleaseDate = null;
                game.OriginalGameName = null;
                game.IgdbVersionParentId = null;
                return;
            }

            // version_parent comes back as expanded object, plain int, JSON null, or absent
            bool parentPresent = root.TryGetProperty("version_parent", out var parentNode);
            Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: version_parent present={parentPresent} kind={parentNode.ValueKind}");

            if (parentPresent && parentNode.ValueKind == JsonValueKind.Object)
            {
                game.IgdbVersionParentId = TryGetInt(parentNode, "id");
                string? parentName = GetString(parentNode, "name");
                DateTime? parentDate = TryGetUnixDate(parentNode, "first_release_date");
                if (parentName != null) game.OriginalGameName = parentName;
                if (parentDate.HasValue) game.OriginalReleaseDate = parentDate;
                Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: version_parent inline → name='{parentName}' date={parentDate:yyyy-MM-dd} parentId={game.IgdbVersionParentId}");
            }
            else if (parentPresent && parentNode.ValueKind == JsonValueKind.Number && parentNode.TryGetInt32(out int parentId))
            {
                game.IgdbVersionParentId = parentId;
                Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: version_parent is plain int={parentId}, fetching separately");
                await FetchAndApplyVersionParentAsync(game, parentId).ConfigureAwait(false);
            }
            else if (root.TryGetProperty("parent_game", out var pgNode) && pgNode.ValueKind == JsonValueKind.Number && pgNode.TryGetInt32(out int parentGameId))
            {
                game.IgdbVersionParentId = parentGameId;
                Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: parent_game={parentGameId} — fetching");
                await FetchAndApplyVersionParentAsync(game, parentGameId).ConfigureAwait(false);
            }
            else
            {
                Debug.WriteLine($"[IGDB] ApplyVersionMetadataAsync: no version_parent, no parent_game — trying name fallback");
                string? igdbName = GetString(root, "name");
                if (!string.IsNullOrWhiteSpace(igdbName))
                {
                    await FetchParentByNameFallbackAsync(game, igdbName).ConfigureAwait(false);
                }
                else
                {
                    Debug.WriteLine("[IGDB] ApplyVersionMetadataAsync: no IGDB name to strip — giving up");
                }
            }
        }

        private async Task FetchParentByNameFallbackAsync(Game game, string igdbName)
        {
            string stripped = StripRemasterSuffix(igdbName);
            Debug.WriteLine($"[IGDB] FetchParentByNameFallbackAsync: igdbName='{igdbName}' → stripped='{stripped}'");

            if (string.IsNullOrWhiteSpace(stripped) || stripped.Equals(igdbName, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine("[IGDB] FetchParentByNameFallbackAsync: suffix strip produced no change — skipping");
                return;
            }

            try
            {
                string escaped = stripped.Replace("\"", "\\\"");
                string body = $"fields name, first_release_date; where name = \"{escaped}\"; limit 1;";
                string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] FetchParentByNameFallbackAsync: response for '{stripped}': {json}");

                if (string.IsNullOrWhiteSpace(json)) return;

                using var doc = JsonDocument.Parse(json);
                var first = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().FirstOrDefault()
                    : default;
                if (first.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FetchParentByNameFallbackAsync: no match found for '{stripped}'");
                    return;
                }

                string? name = GetString(first, "name");
                DateTime? date = TryGetUnixDate(first, "first_release_date");
                if (name != null) game.OriginalGameName = name;
                if (date.HasValue) game.OriginalReleaseDate = date;
                Debug.WriteLine($"[IGDB] FetchParentByNameFallbackAsync: set originalName='{name}' originalDate={date:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FetchParentByNameFallbackAsync: EXCEPTION for '{stripped}': {ex.Message}");
            }
        }

        private static string StripRemasterSuffix(string name)
        {
            // Longer/more specific patterns first to avoid partial matches
            string[] suffixes =
            {
                ": HD Remaster", ": Remastered Edition", ": Remastered", ": Remaster",
                ": Remake", ": HD Edition", ": HD",
                " - HD Remaster", " - Remastered Edition", " - Remastered", " - Remaster",
                " - Remake", " - HD Edition", " - HD",
                " HD Remaster", " Remastered Edition", " Remastered", " Remaster",
                " Remake", " HD Edition",
                " Anniversary Edition", " Definitive Edition", " Complete Edition",
                " Director's Cut", " (Remastered)", " (HD)"
            };
            foreach (string suffix in suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^suffix.Length].Trim(' ', '-', ':', '–', '—');
                }
            }
            return name;
        }

        private async Task FetchAndApplyVersionParentAsync(Game game, int parentId)
        {
            Debug.WriteLine($"[IGDB] FetchAndApplyVersionParentAsync: fetching parentId={parentId}");
            try
            {
                string body = $"fields name, first_release_date; where id = {parentId}; limit 1;";
                string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] FetchAndApplyVersionParentAsync: response for parentId={parentId}: {json}");

                if (string.IsNullOrWhiteSpace(json)) return;

                using var doc = JsonDocument.Parse(json);
                var first = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().FirstOrDefault()
                    : default;
                if (first.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FetchAndApplyVersionParentAsync: no result for parentId={parentId}");
                    return;
                }

                string? name = GetString(first, "name");
                DateTime? date = TryGetUnixDate(first, "first_release_date");
                if (name != null) game.OriginalGameName = name;
                if (date.HasValue) game.OriginalReleaseDate = date;
                Debug.WriteLine($"[IGDB] FetchAndApplyVersionParentAsync: set originalName='{name}' originalDate={date:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FetchAndApplyVersionParentAsync: EXCEPTION for parentId={parentId}: {ex.Message}");
            }
        }

        private async Task FetchAndStoreFranchiseGamesAsync(Game game, int franchiseId)
        {
            Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: franchiseId={franchiseId}");
            try
            {
                // Step 1: get the game IDs in this franchise
                string franchiseBody = $"fields games, name; where id = {franchiseId}; limit 1;";
                string franchiseJson = await PostAsync(CollectionsEndpoint, franchiseBody).ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: franchise response: {franchiseJson}");

                if (string.IsNullOrWhiteSpace(franchiseJson)) return;

                using var franchiseDoc = JsonDocument.Parse(franchiseJson);
                var franchiseEl = franchiseDoc.RootElement.ValueKind == JsonValueKind.Array
                    ? franchiseDoc.RootElement.EnumerateArray().FirstOrDefault()
                    : default;
                if (franchiseEl.ValueKind != JsonValueKind.Object)
                {
                    Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: no franchise object for id={franchiseId}");
                    return;
                }

                if (!franchiseEl.TryGetProperty("games", out var gamesNode) || gamesNode.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: no 'games' array in franchise id={franchiseId}");
                    return;
                }

                var ids = new List<int>();
                foreach (var idEl in gamesNode.EnumerateArray())
                {
                    if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out int gid))
                        ids.Add(gid);
                }
                Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: franchise has {ids.Count} game IDs: [{string.Join(",", ids)}]");

                if (ids.Count == 0) return;

                // Step 2: fetch name + dates for all franchise games in one call
                string idList = string.Join(",", ids);
                string gamesBody = $@"fields
  id,
  name,
  first_release_date,
  game_type,
  game_type.type,
  version_parent.first_release_date,
  cover.image_id,
  platforms.name;
where id = ({idList});
sort first_release_date asc;
limit 200;";

                string gamesJson = await PostAsync(GamesEndpoint, gamesBody).ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: games response: {gamesJson}");

                if (string.IsNullOrWhiteSpace(gamesJson)) return;

                using var gamesDoc = JsonDocument.Parse(gamesJson);
                if (gamesDoc.RootElement.ValueKind != JsonValueKind.Array) return;

                var entries = new List<FranchiseGameRef>();
                foreach (var item in gamesDoc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string? entryName = GetString(item, "name");
                    if (string.IsNullOrWhiteSpace(entryName)) continue;

                    int? entryId = TryGetInt(item, "id");
                    if (!entryId.HasValue) continue;

                    DateTime? releaseDate = TryGetUnixDate(item, "first_release_date");

                    int? gameTypeId = null;
                    string? gameTypeStr = null;
                    if (item.TryGetProperty("game_type", out var gtNode))
                    {
                        if (gtNode.ValueKind == JsonValueKind.Object)
                        {
                            gameTypeId = TryGetInt(gtNode, "id");
                            gameTypeStr = NormalizeGameType(GetString(gtNode, "type"));
                        }
                        else if (gtNode.ValueKind == JsonValueKind.Number && gtNode.TryGetInt32(out int gtInt))
                        {
                            gameTypeId = gtInt;
                        }
                    }

                    DateTime? originalDate = null;
                    if (item.TryGetProperty("version_parent", out var vpNode) && vpNode.ValueKind == JsonValueKind.Object)
                        originalDate = TryGetUnixDate(vpNode, "first_release_date");

                    string? coverImageId = null;
                    if (item.TryGetProperty("cover", out var coverNode) && coverNode.ValueKind == JsonValueKind.Object)
                        coverImageId = GetString(coverNode, "image_id");

                    Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: entry id={entryId} name='{entryName}' type='{gameTypeStr}' release={releaseDate:yyyy-MM-dd} originalDate={originalDate:yyyy-MM-dd}");
                    var entryPlatforms = GetNameList(item, "platforms");

                    entries.Add(new FranchiseGameRef(
                        IgdbId: entryId.Value,
                        Name: entryName,
                        ReleaseDate: releaseDate,
                        OriginalReleaseDate: originalDate,
                        CategoryName: FormatGameTypeName(gameTypeStr),
                        CoverUrl: coverImageId != null ? BuildImageUrl(coverImageId, "t_cover_big") : null,
                        IgdbCategory: gameTypeId ?? MapGameTypeToCategory(gameTypeStr),
                        Platforms: entryPlatforms.Count > 0 ? entryPlatforms : null
                    ));
                }

                game.FranchiseGames = entries.Count > 0 ? entries : null;
                Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: stored {entries.Count} entries on game");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IGDB] FetchAndStoreFranchiseGamesAsync: EXCEPTION for franchiseId={franchiseId}: {ex.Message}");
            }
        }

        private async Task<string> PostAsync(string url, string body)
        {
            Debug.WriteLine($"[IGDB] POST {url}");
            Debug.WriteLine($"[IGDB] BODY: {body}");
            using var content = new StringContent(body, Encoding.UTF8, "text/plain");
            using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
            Debug.WriteLine($"[IGDB] HTTP {(int)response.StatusCode} {response.StatusCode} ← {url}");
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"[IGDB] ERROR RESPONSE: {errorBody}");
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Response content: {errorBody}");
            }
            string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Debug.WriteLine($"[IGDB] RESPONSE: {result}");
            return result;
        }

        private static string BuildImageUrl(string imageId, string size)
            => $"https://images.igdb.com/igdb/image/upload/{size}/{imageId}.jpg";

        private static string? GetString(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
            return null;
        }

        private static int? TryGetInt(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
                {
                    return val;
                }
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        private static int? TryGetLinkedId(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var prop))
            {
                return null;
            }

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int id))
            {
                return id;
            }

            if (prop.ValueKind == JsonValueKind.Object)
            {
                return TryGetInt(prop, "id");
            }

            return null;
        }

        private static DateTime? TryGetUnixDate(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetInt64(out long seconds) &&
                seconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }

            return null;
        }

        private static bool ReleaseYearMatches(IReadOnlySet<int>? allowedReleaseYears, IReadOnlySet<int> candidateYears)
        {
            if (allowedReleaseYears is null || allowedReleaseYears.Count == 0)
            {
                return true;
            }

            if (candidateYears.Count == 0)
            {
                return false;
            }

            return candidateYears.Any(cy => allowedReleaseYears.Any(ay => Math.Abs(cy - ay) <= 1));
        }

        private static string FormatYears(IReadOnlySet<int>? years)
        {
            if (years is null || years.Count == 0)
            {
                return "?";
            }

            return string.Join("/", years.Order());
        }

        private static string? FormatGameTypeName(string? gameType)
        {
            string? normalized = NormalizeGameType(gameType);
            return normalized switch
            {
                "main_game" => "Main Game",
                "dlc_addon" => "DLC/Add-on",
                "expansion" => "Expansion",
                "bundle" => "Bundle",
                "standalone_expansion" => "Standalone Expansion",
                "mod" => "Mod",
                "episode" => "Episode",
                "season" => "Season",
                "remake" => "Remake",
                "remaster" => "Remaster",
                "expanded_game" => "Expanded Game",
                "port" => "Port",
                "fork" => "Fork",
                "pack" => "Pack",
                "update" => "Update",
                _ => gameType
            };
        }

        private static int? MapGameTypeToCategory(string? gameType) => NormalizeGameType(gameType) switch
        {
            "main_game" => 0,
            "dlc_addon" => 1,
            "expansion" => 2,
            "bundle" => 3,
            "standalone_expansion" => 4,
            "mod" => 5,
            "episode" => 6,
            "season" => 7,
            "remake" => 8,
            "remaster" => 9,
            "expanded_game" => 10,
            "port" => 11,
            "fork" => 12,
            "pack" => 13,
            "update" => 14,
            _ => null
        };

        private static List<string> GetNameList(JsonElement root, string arrayProperty)
        {
            var list = new List<string>();
            if (root.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    string? name = GetString(item, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add(name);
                    }
                }
            }
            return list;
        }

        private static bool ShouldOverwrite(string? existing, int? steamId)
        {
            if (!steamId.HasValue)
            {
                return true;
            }
            return string.IsNullOrWhiteSpace(existing);
        }

        private static bool IsPlaceholder(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            return url.Contains("placehold.co", StringComparison.OrdinalIgnoreCase);
        }

        private static string TruncateWithEllipsis(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }
            if (maxLength <= 3)
            {
                return new string('.', Math.Max(0, maxLength));
            }
            return value.Substring(0, maxLength).TrimEnd() + "...";
        }

        // ── Franchise timeline ────────────────────────────────────────────────

        /// <summary>
        /// Returns all main games, remakes, and remasters in a franchise,
        /// sorted by release date ascending. Each entry carries both the
        /// game's own release date and (for remakes/remasters) the original date.
        /// </summary>
        public async Task<List<FranchiseEntry>> FetchFranchiseTimelineAsync(int franchiseId)
        {
            try
            {
                string body = $@"fields
  name,
  slug,
  first_release_date,
  game_type.type,
  version_parent.name,
  version_parent.first_release_date,
  cover.image_id;
where collections = ({franchiseId});
sort first_release_date asc;
limit 200;";

                string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<FranchiseEntry>();
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return new List<FranchiseEntry>();
                }

                var entries = new List<FranchiseEntry>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string? name = GetString(item, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    DateTime? releaseDate = TryGetUnixDate(item, "first_release_date");

                    string? gameTypeStr = null;
                    if (item.TryGetProperty("game_type", out var gtNode) && gtNode.ValueKind == JsonValueKind.Object)
                        gameTypeStr = NormalizeGameType(GetString(gtNode, "type"));

                    int? igdbCategory = MapGameTypeToCategory(gameTypeStr);

                    string? originalName = null;
                    DateTime? originalDate = null;
                    if (item.TryGetProperty("version_parent", out var vpNode) && vpNode.ValueKind == JsonValueKind.Object)
                    {
                        originalName = GetString(vpNode, "name");
                        originalDate = TryGetUnixDate(vpNode, "first_release_date");
                    }

                    string? imageId = null;
                    if (item.TryGetProperty("cover", out var coverNode) && coverNode.ValueKind == JsonValueKind.Object)
                        imageId = GetString(coverNode, "image_id");

                    entries.Add(new FranchiseEntry(
                        Name: name,
                        ReleaseDate: releaseDate,
                        IgdbCategory: igdbCategory,
                        CategoryName: FormatGameTypeName(gameTypeStr),
                        OriginalGameName: originalName,
                        OriginalReleaseDate: originalDate,
                        CoverUrl: imageId != null ? BuildImageUrl(imageId, "t_cover_big") : null
                    ));
                }

                return entries;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IGDB franchise timeline fetch failed for franchise={franchiseId}: {ex.Message}");
                return new List<FranchiseEntry>();
            }
        }
    }

    public sealed record FranchiseEntry(
        string Name,
        DateTime? ReleaseDate,
        int? IgdbCategory,
        string? CategoryName,
        string? OriginalGameName,
        DateTime? OriginalReleaseDate,
        string? CoverUrl
    );
}
