using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codec.Models;

namespace Codec.Services
{
    public static class HltbService
    {
        private const string SearchEndpoint = "https://codec-api-proxy.vercel.app/api/hltb/search?term=";
        private static readonly HttpClient Http = new();

        public static async Task PopulateAsync(Game game, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (game == null)
            {
                return;
            }

            // Skip if we already have both values
            if (game.TimeToCompleteMainStory.HasValue && game.TimeToCompleteCompletionist.HasValue)
            {
                return;
            }

            var term = NormalizeSearchTerm(game.Name);
            if (string.IsNullOrWhiteSpace(term))
            {
                return;
            }

            try
            {
                var url = SearchEndpoint + Uri.EscapeDataString(term);
                var json = await DataCacheService.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                {
                    return;
                }

                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                JsonElement? best = null;
                double bestScore = double.MinValue;

                foreach (var candidate in results.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string candidateName = nameProp.GetString() ?? string.Empty;
                    double similarity = CalculateNameSimilarity(term, candidateName);

                    if (candidate.TryGetProperty("similarity", out var apiScore) && apiScore.TryGetDouble(out double s))
                    {
                        similarity = Math.Max(similarity, s);
                    }

                    if (similarity > bestScore)
                    {
                        bestScore = similarity;
                        best = candidate;
                    }
                }

                if (best == null)
                {
                    return;
                }

                var chosen = best.Value;

                int? mainTime = GetInt(chosen, "mainTime");
                int? completionist = GetInt(chosen, "completionistTime");
                string? hltbUrl = null;

                if (chosen.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out int id))
                {
                    hltbUrl = $"https://howlongtobeat.com/game/{id}";
                }
                else if (chosen.TryGetProperty("imageUrl", out var imgProp) && imgProp.ValueKind == JsonValueKind.String)
                {
                    // fallback: derive base site
                    hltbUrl = "https://howlongtobeat.com";
                }

                void ApplyUpdates()
                {
                    if (mainTime.HasValue)
                    {
                        game.TimeToCompleteMainStory = mainTime.Value;
                        game.NotifyPropertyChanged(nameof(game.TimeToCompleteMainStory));
                    }

                    if (completionist.HasValue)
                    {
                        game.TimeToCompleteCompletionist = completionist.Value;
                        game.NotifyPropertyChanged(nameof(game.TimeToCompleteCompletionist));
                    }

                    if (!string.IsNullOrWhiteSpace(hltbUrl))
                    {
                        game.HltbUrl = hltbUrl;
                    }

                    game.LastUpdated = DateTime.Now;
                }

                if (dispatcher != null)
                {
                    dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, ApplyUpdates);
                }
                else
                {
                    ApplyUpdates();
                }
            }
            catch
            {
                // Ignore errors; leave existing values intact
            }
        }

        private static int? GetInt(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val))
                {
                    return val;
                }

                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int strVal))
                {
                    return strVal;
                }
            }

            return null;
        }

        private static double CalculateNameSimilarity(string reference, string candidate)
        {
            string normRef = Normalize(reference);
            string normCand = Normalize(candidate);

            if (normRef.Length == 0 || normCand.Length == 0)
            {
                return 0;
            }

            int distance = LevenshteinDistance(normRef, normCand);
            int maxLength = Math.Max(normRef.Length, normCand.Length);
            return maxLength > 0 ? 1.0 - ((double)distance / maxLength) : 0;
        }

        private static string Normalize(string value)
        {
            string lower = value.ToLowerInvariant();
            lower = Regex.Replace(lower, "[^a-z0-9\\s]", " ");
            lower = Regex.Replace(lower, "\\s+", " ").Trim();
            return lower;
        }

        private static string NormalizeSearchTerm(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            // Keep only letters, numbers, and spaces for the query.
            string cleaned = Regex.Replace(value, "[^a-zA-Z0-9 ]", " ");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned;
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            int[,] d = new int[source.Length + 1, target.Length + 1];

            for (int i = 0; i <= source.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[source.Length, target.Length];
        }
    }
}
