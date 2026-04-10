using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codec.Models;
using Codec.Services.Storage;

namespace Codec.Services.Fetching
{
    public class HltbService
    {
        private const string SearchEndpoint = "https://codec-api-proxy.vercel.app/api/hltb/search?term=";
        private readonly HttpClient _http = new();
        private readonly MetadataCache _cache;

        public HltbService(MetadataCache cache)
        {
            _cache = cache;
        }

        public async Task PopulateAsync(Game game, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (game == null)
            {
                return;
            }

            // Skip only if we already have times AND a link; allow rerun to backfill missing link
            if (game.TimeToCompleteMainStory.HasValue && game.TimeToCompleteCompletionist.HasValue && !string.IsNullOrWhiteSpace(game.HltbUrl))
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
                var json = await _cache.GetOrFetchAsync("hltb", url, TimeSpan.FromDays(7)).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                {
                    return;
                }

                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                var candidates = results.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Object)
                    .Select(c => new
                    {
                        Element = c,
                        Name = c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty,
                        Similarity = GetSimilarity(c, term),
                        ReleaseYear = GetInt(c, "releaseYear")
                    })
                    .ToList();

                if (candidates.Count == 0)
                {
                    return;
                }

                var exact = candidates.FirstOrDefault(c => Math.Abs(c.Similarity - 1.0) < 0.0001);
                var targetYear = game.ReleaseDate?.Year;
                var yearMatch = targetYear.HasValue
                    ? candidates.Where(c => c.ReleaseYear.HasValue && c.ReleaseYear.Value == targetYear.Value)
                                 .OrderByDescending(c => c.Similarity)
                                 .FirstOrDefault()
                    : null;
                var best = candidates.OrderByDescending(c => c.Similarity).First();

                var chosen = (exact ?? yearMatch ?? best);

                int? mainTime = GetInt(chosen.Element, "mainTime");
                int? completionist = GetInt(chosen.Element, "completionistTime");
                string? hltbUrl = null;

                if (chosen.Element.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out int id))
                {
                    hltbUrl = $"https://howlongtobeat.com/game/{id}";
                }
                else if (chosen.Element.TryGetProperty("imageUrl", out var imgProp) && imgProp.ValueKind == JsonValueKind.String)
                {
                    // fallback: derive base site
                    hltbUrl = "https://howlongtobeat.com";
                }

                void ApplyUpdates()
                {
                    if (mainTime.HasValue)
                    {
                        game.TimeToCompleteMainStory = mainTime.Value;
                    }

                    if (completionist.HasValue)
                    {
                        game.TimeToCompleteCompletionist = completionist.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(hltbUrl))
                    {
                        game.HltbUrl = hltbUrl;
                    }
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

        private int? GetInt(JsonElement element, string propertyName)
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

        private double GetSimilarity(JsonElement candidate, string referenceName)
        {
            double similarity = 0;

            if (candidate.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                similarity = CalculateNameSimilarity(referenceName, nameProp.GetString() ?? string.Empty);
            }

            if (candidate.TryGetProperty("similarity", out var apiScore) && apiScore.TryGetDouble(out double s))
            {
                similarity = Math.Max(similarity, s);
            }

            return similarity;
        }

        private double CalculateNameSimilarity(string reference, string candidate)
        {
            string normRef = Normalize(reference);
            string normCand = Normalize(candidate);

            if (normRef.Length == 0 || normCand.Length == 0)
                return 0;

            return Helpers.StringSimilarity.Calculate(normRef, normCand);
        }

        private string Normalize(string value)
        {
            string lower = value.ToLowerInvariant();
            lower = Regex.Replace(lower, "[^a-z0-9\\s]", " ");
            lower = Regex.Replace(lower, "\\s+", " ").Trim();
            return lower;
        }

        private string NormalizeSearchTerm(string? value)
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

    }
}
