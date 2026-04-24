using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codec.Models;

namespace Codec.Services.Fetching
{
    public class IgdbService
    {
        private static readonly Regex TrademarkRegex = new(@"\(TM\)|\(R\)|™|®|©", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string ProxyBase = "https://codec-api-proxy.vercel.app/api/igdb";
        private const string ExternalGamesEndpoint = ProxyBase + "/external_games";
        private const string GamesEndpoint = ProxyBase + "/games";
        private const string TimeToBeatsEndpoint = ProxyBase + "/game_time_to_beats";
        private const string ArtworksEndpoint = ProxyBase + "/artworks";

        private readonly HttpClient _http = new();

        public async Task<int?> FindIgdbIdByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            name = Regex.Replace(TrademarkRegex.Replace(name, ""), @"\s+", " ").Trim();

            try
            {
                string escaped = name.Replace("\"", "\\\"");
                string body = $"search \"{escaped}\"; fields id, name; limit 1;";
                string json = await PostAsync(GamesEndpoint, body).ConfigureAwait(false);
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

                return TryGetInt(first, "id");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IGDB name search failed for '{name}': {ex.Message}");
                return null;
            }
        }

        public async Task<int?> FindIgdbIdBySteamIdAsync(int steamId)
        {
            try
            {
                string body = $"fields name, game; where uid = \"{steamId}\" & external_game_source = 1; limit 1;";
                string json = await PostAsync(ExternalGamesEndpoint, body).ConfigureAwait(false);
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

                return TryGetInt(first, "game");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IGDB external_games lookup failed for steam={steamId}: {ex.Message}");
                return null;
            }
        }

        public async Task PopulateFromIgdbAsync(Game game)
        {
            if (game == null || !game.IgdbId.HasValue)
            {
                return;
            }

            int igdbId = game.IgdbId.Value;

            var gameTask = FetchGameAsync(igdbId);
            var timeTask = FetchTimeToBeatAsync(igdbId);

            await Task.WhenAll(gameTask, timeTask).ConfigureAwait(false);

            ApplyGameDetails(game, gameTask.Result);
            ApplyTimeToBeat(game, timeTask.Result);

            // Artworks: only if Steam didn't provide a hero (non-Steam path)
            if (!game.SteamID.HasValue && (string.IsNullOrWhiteSpace(game.LibHeroUrl) || IsPlaceholder(game.LibHeroUrl)))
            {
                string? artworkUrl = await FetchFirstArtworkAsync(igdbId).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    game.LibHeroUrl = artworkUrl;
                }
            }
        }

        private async Task<JsonElement?> FetchGameAsync(int igdbId)
        {
            try
            {
                string body = $@"fields
  name,
  slug,
  summary,
  storyline,
  first_release_date,
  url,
  cover.image_id,
  artworks.image_id,
  screenshots.image_id,
  videos.video_id,
  videos.name,
  genres.name,
  platforms.name,
  franchises.name,
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

                return first.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IGDB games fetch failed for id={igdbId}: {ex.Message}");
                return null;
            }
        }

        private async Task<JsonElement?> FetchTimeToBeatAsync(int igdbId)
        {
            try
            {
                string body = $"fields completely,count,game_id,hastily,normally; where game_id = {igdbId}; limit 1;";
                string json = await PostAsync(TimeToBeatsEndpoint, body).ConfigureAwait(false);
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

                return first.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IGDB time_to_beats fetch failed for id={igdbId}: {ex.Message}");
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

            // Release date (Unix seconds) — always set, Steam's release fallback only runs when null
            if (root.TryGetProperty("first_release_date", out var releaseNode) && releaseNode.ValueKind == JsonValueKind.Number)
            {
                if (releaseNode.TryGetInt64(out long unixSecs))
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(unixSecs).UtcDateTime;
                    if (ShouldOverwrite(game.ReleaseDate == null ? null : game.ReleaseDate.Value.ToString(), game.SteamID) || !game.ReleaseDate.HasValue)
                    {
                        game.ReleaseDate = dt;
                    }
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
            bool mediaIsPlaceholder = game.Media == null || game.Media.Count == 0 || game.Media.All(m => m.Contains("placehold.co", StringComparison.OrdinalIgnoreCase));
            if (mediaIsPlaceholder)
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

        private async Task<string> PostAsync(string url, string body)
        {
            using var content = new StringContent(body, Encoding.UTF8, "text/plain");
            using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
    }
}
