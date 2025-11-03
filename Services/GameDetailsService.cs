using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services
{
    /// <summary>
    /// Service for validating games via RAWG.io API
    /// </summary>
    public static class GameDetailsService
    {
        private static readonly HttpClient _httpClient = new();
        private const string RawgSearchUrl = "https://codec-api-proxy.vercel.app/api/rawg/search";
        private const double MinimumSimilarity = 0.90; // 90% match required

        /// <summary>
        /// Validates if a game name exists in RAWG database and matches the name
        /// </summary>
        /// <param name="gameName">The game name to validate</param>
        /// <returns>RAWG ID if game is valid and name matches ?90%, null otherwise</returns>
        public static async Task<int?> ValidateGameAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return null;

            // Special case: Fortnite -> Fortnite Battle Royale
            string searchName = ApplyGameNameOverrides(gameName);
            
            try
            {
                string url = $"{RawgSearchUrl}?term={Uri.EscapeDataString(searchName)}";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);

                if (!doc.RootElement.TryGetProperty("results", out var results))
                    return null;

                var resultsArray = results.EnumerateArray().ToArray();
                if (resultsArray.Length == 0)
                    return null;

                // Get first result
                var firstResult = resultsArray[0];
                if (!firstResult.TryGetProperty("id", out var idProp) ||
                    !firstResult.TryGetProperty("name", out var nameProp))
                    return null;

                int gameId = idProp.GetInt32();
                string? rawgName = nameProp.GetString();

                if (string.IsNullOrEmpty(rawgName))
                    return null;

                // Validate name similarity (must be ?90%)
                // Use searchName (with overrides) for comparison
                double similarity = CalculateNameSimilarity(searchName, rawgName);

                if (similarity >= MinimumSimilarity)
                {
                    Debug.WriteLine($"? Game validated: '{gameName}' -> '{searchName}' matches '{rawgName}' ({similarity:P}) - RAWG ID: {gameId}");
                    return gameId;
                }
                else
                {
                    Debug.WriteLine($"? Game rejected: '{searchName}' vs '{rawgName}' ({similarity:P}) - Below 90% threshold");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error validating game '{gameName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Applies special name overrides for specific games
        /// </summary>
        private static string ApplyGameNameOverrides(string gameName)
        {
            string normalized = gameName.Trim().ToLowerInvariant();

            // Fortnite -> Fortnite Battle Royale
            if (normalized.Contains("fortnite") && !normalized.Contains("battle royale"))
            {
                Debug.WriteLine($"  [NAME OVERRIDE] '{gameName}' -> 'Fortnite Battle Royale'");
                return "Fortnite Battle Royale";
            }

            return gameName;
        }

        /// <summary>
        /// Calculates similarity between two game names using Levenshtein distance
        /// </summary>
        private static double CalculateNameSimilarity(string name1, string name2)
        {
            // Normalize names for comparison
            string normalized1 = NormalizeName(name1);
            string normalized2 = NormalizeName(name2);

            int distance = LevenshteinDistance(normalized1, normalized2);
            int maxLength = Math.Max(normalized1.Length, normalized2.Length);

            return maxLength > 0 ? 1.0 - ((double)distance / maxLength) : 0.0;
        }

        /// <summary>
        /// Normalizes a game name for comparison
        /// </summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            // Convert to lowercase
            name = name.ToLowerInvariant();

            // Remove common edition/version words
            var removeWords = new[] {
                "edition", "remastered", "goty", "complete", "definitive",
                "enhanced", "special", "digital", "deluxe", "ultimate",
                "game of the year", "directors cut", "gold", "redux",
                "the", "a", "an"
            };

            foreach (var word in removeWords)
            {
                name = name.Replace(word, " ");
            }

            // Remove special characters
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[^\w\s]", " ");

            // Remove extra spaces
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ");

            return name.Trim();
        }

        /// <summary>
        /// Calculates Levenshtein distance between two strings
        /// </summary>
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
    }
}
