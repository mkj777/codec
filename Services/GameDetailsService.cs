using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Codec.Services
{
    public enum RawgValidationMode
    {
        Strict,
        SteamBacked
    }

    /// <summary>
    /// Service for validating games via RAWG.io API
    /// </summary>
    public static class GameDetailsService
    {
        private static readonly HttpClient _httpClient = new();
        private const string RawgSearchUrl = "https://codec-api-proxy.vercel.app/api/rawg/search";
        private const int DefaultPageSize = 5;
        private const double StrictScoreThreshold = 0.88;
        private const double SteamBackedScoreThreshold = 0.82;
        private const double MinimumScoreDelta = 0.08;
        private const int MinimumRatingsCount = 5;

        /// <summary>
        /// Validates if a game name exists in RAWG database with strict filtering and scoring.
        /// </summary>
        public static async Task<int?> ValidateGameAsync(string gameName, RawgValidationMode mode = RawgValidationMode.Strict)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            string searchName = ApplyGameNameOverrides(gameName);
            var settings = RawgValidationSettings.FromMode(mode);

            try
            {
                string url = BuildSearchUrl(searchName, settings.PageSize);
                var response = await DataCacheService.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);

                if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var scoredResults = ScoreRawgResults(resultsElement, searchName, settings).OrderByDescending(r => r.Score).ToList();
                if (scoredResults.Count == 0)
                {
                    return null;
                }

                var best = scoredResults[0];
                var runnerUp = scoredResults.Count > 1 ? scoredResults[1] : null;

                if (best.Score < settings.MinimumScore)
                {
                    Debug.WriteLine($"  ✗ RAWG reject: '{searchName}' best score {best.Score:F2} below threshold {settings.MinimumScore:F2}");
                    return null;
                }

                if (runnerUp != null && best.Score - runnerUp.Score < settings.MinimumDelta)
                {
                    Debug.WriteLine($"  ✗ RAWG reject: '{searchName}' ambiguous match (Δ {best.Score - runnerUp.Score:F2})");
                    return null;
                }

                Debug.WriteLine($"  ✓ RAWG validated '{searchName}' -> '{best.Name}' ({best.Score:F2}) ID {best.RawgId}");
                return best.RawgId;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"  ✗ RAWG validation error for '{gameName}': {ex.Message}");
                return null;
            }
        }

        private static string BuildSearchUrl(string searchName, int pageSize)
        {
            var query = new List<string>
            {
                $"term={Uri.EscapeDataString(searchName)}",
                $"page_size={pageSize}",
                "ordering=-added",
                "exclude_additions=true",
                "exclude_game_series=true",
                "exclude_parents=true",
                "platforms=4",
                "parent_platforms=1"
            };

            return $"{RawgSearchUrl}?{string.Join("&", query)}";
        }

        private static IEnumerable<RawgCandidate> ScoreRawgResults(JsonElement results, string searchName, RawgValidationSettings settings)
        {
            foreach (var result in results.EnumerateArray().Take(settings.PageSize))
            {
                if (TryCreateCandidate(result, searchName, out var candidate))
                {
                    yield return candidate;
                }
            }
        }

        private static bool TryCreateCandidate(JsonElement element, string queryName, out RawgCandidate candidate)
        {
            candidate = default!;

            if (!element.TryGetProperty("id", out var idProp) || !element.TryGetProperty("name", out var nameProp))
            {
                return false;
            }

            int id = idProp.GetInt32();
            string? name = nameProp.GetString();
            if (id <= 0 || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            int ratingsCount = element.TryGetProperty("ratings_count", out var ratingsProp) && ratingsProp.TryGetInt32(out var ratings)
                ? ratings
                : 0;

            if (ratingsCount < MinimumRatingsCount)
            {
                Debug.WriteLine($"  ✗ RAWG reject: '{name}' insufficient ratings ({ratingsCount})");
                return false;
            }

            if (!PassesPlatformFilter(element))
            {
                return false;
            }

            if (IsAdditionOrSeries(element))
            {
                return false;
            }

            double similarity = CalculateNameSimilarity(queryName, name);
            double releaseBoost = CalculateReleaseBoost(queryName, element);
            double popularityBoost = CalculatePopularityBoost(element);

            double finalScore = Math.Clamp(similarity + releaseBoost + popularityBoost, 0, 1);

            candidate = new RawgCandidate(id, name, finalScore);
            return true;
        }

        private static bool PassesPlatformFilter(JsonElement element)
        {
            if (HasParentPlatform(element, 1))
            {
                return true;
            }

            return HasPlatform(element, 4);
        }

        private static bool HasParentPlatform(JsonElement element, int platformId)
        {
            if (!element.TryGetProperty("parent_platforms", out var parentPlatforms) || parentPlatforms.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var entry in parentPlatforms.EnumerateArray())
            {
                if (entry.TryGetProperty("platform", out var platform) &&
                    platform.TryGetProperty("id", out var idProp) &&
                    idProp.TryGetInt32(out int id) && id == platformId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPlatform(JsonElement element, int platformId)
        {
            if (!element.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var entry in platforms.EnumerateArray())
            {
                if (entry.TryGetProperty("platform", out var platform) &&
                    platform.TryGetProperty("id", out var idProp) &&
                    idProp.TryGetInt32(out int id) && id == platformId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAdditionOrSeries(JsonElement element)
        {
            int additions = element.TryGetProperty("additions_count", out var additionsProp) && additionsProp.TryGetInt32(out var a) ? a : 0;
            int dlc = element.TryGetProperty("dlc_count", out var dlcProp) && dlcProp.TryGetInt32(out var d) ? d : 0;
            int series = element.TryGetProperty("game_series_count", out var seriesProp) && seriesProp.TryGetInt32(out var s) ? s : 0;

            return additions > 0 || dlc > 0 || series > 0;
        }

        private static double CalculateReleaseBoost(string queryName, JsonElement element)
        {
            int? queryYear = ExtractYear(queryName);
            if (!queryYear.HasValue)
            {
                return 0;
            }

            int? releaseYear = null;
            if (element.TryGetProperty("released", out var releasedProp))
            {
                var releasedValue = releasedProp.GetString();
                if (!string.IsNullOrWhiteSpace(releasedValue) && DateTime.TryParse(releasedValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
                {
                    releaseYear = releaseDate.Year;
                }
            }

            if (!releaseYear.HasValue)
            {
                return 0;
            }

            int diff = Math.Abs(releaseYear.Value - queryYear.Value);
            if (diff == 0)
            {
                return 0.05;
            }

            if (diff == 1)
            {
                return 0.02;
            }

            return diff > 3 ? -0.05 : 0;
        }

        private static double CalculatePopularityBoost(JsonElement element)
        {
            if (element.TryGetProperty("ratings_count", out var ratingsProp) && ratingsProp.TryGetInt32(out var ratings))
            {
                if (ratings > 10000) return 0.04;
                if (ratings > 2000) return 0.02;
            }

            return 0;
        }

        private static int? ExtractYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = Regex.Match(value, @"(19|20)\d{2}");
            if (match.Success && int.TryParse(match.Value, out int year))
            {
                return year;
            }

            return null;
        }

        /// <summary>
        /// Applies special name overrides for specific games
        /// </summary>
        private static string ApplyGameNameOverrides(string gameName)
        {
            string normalized = gameName.Trim().ToLowerInvariant();

            if (normalized.Contains("fortnite") && !normalized.Contains("battle royale"))
            {
                Debug.WriteLine($"  [NAME OVERRIDE] '{gameName}' -> 'Fortnite Battle Royale'");
                return "Fortnite Battle Royale";
            }

            return gameName;
        }

        private static double CalculateNameSimilarity(string name1, string name2)
        {
            string normalized1 = NormalizeName(name1);
            string normalized2 = NormalizeName(name2);

            int distance = LevenshteinDistance(normalized1, normalized2);
            int maxLength = Math.Max(normalized1.Length, normalized2.Length);

            return maxLength > 0 ? 1.0 - ((double)distance / maxLength) : 0.0;
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            name = name.ToLowerInvariant();

            var removeWords = new[]
            {
                "edition", "remastered", "goty", "complete", "definitive",
                "enhanced", "special", "digital", "deluxe", "ultimate",
                "game of the year", "directors cut", "gold", "redux",
                "the", "a", "an"
            };

            foreach (var word in removeWords)
            {
                name = name.Replace(word, " ");
            }

            name = Regex.Replace(name, @"[^\w\s]", " ");
            name = Regex.Replace(name, @"\s+", " ");

            return name.Trim();
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            var d = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; i++)
                d[i, 0] = i;

            for (int j = 0; j <= target.Length; j++)
                d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost
                    );
                }
            }

            return d[source.Length, target.Length];
        }

        private sealed record RawgCandidate(int RawgId, string Name, double Score);

        private sealed record RawgValidationSettings(double MinimumScore, double MinimumDelta, int PageSize)
        {
            public static RawgValidationSettings FromMode(RawgValidationMode mode) => mode switch
            {
                RawgValidationMode.SteamBacked => new RawgValidationSettings(SteamBackedScoreThreshold, MinimumScoreDelta, DefaultPageSize),
                _ => new RawgValidationSettings(StrictScoreThreshold, MinimumScoreDelta, DefaultPageSize)
            };
        }
    }
}
