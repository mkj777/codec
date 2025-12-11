using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Codec.Models;

namespace Codec.Services
{
    public static class SteamDetailsService
    {
        private static readonly HttpClient Http = new HttpClient();

        private static async Task<string?> ResolveAssetUrlAsync(string primaryUrl, string fallbackUrl)
        {
            async Task<bool> IsReachableAsync(string url)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    // Some CDN paths return a 404 HTML body with a 200 OK. Detect simple HTML 404 content.
                    if ((int)response.StatusCode == 404)
                    {
                        return false;
                    }

                    // Fallback: try a lightweight GET to validate actual body is not an HTML 404.
                    using var getResponse = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!getResponse.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    if (getResponse.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return false;
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (await IsReachableAsync(primaryUrl).ConfigureAwait(false))
            {
                return primaryUrl;
            }

            if (await IsReachableAsync(fallbackUrl).ConfigureAwait(false))
            {
                return fallbackUrl;
            }

            return null;
        }

        public static async Task PopulateFromSteamAsync(Game game)
        {
            if (!game.SteamID.HasValue)
            {
                return;
            }

            void AddRating(JsonElement root, string key, string label, List<string> ratingList)
            {
                if (root.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.Object)
                {
                    if (r.TryGetProperty("rating", out var ratingVal) && ratingVal.ValueKind == JsonValueKind.String)
                    {
                        var val = ratingVal.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            if (label.Equals("ESRB", StringComparison.OrdinalIgnoreCase))
                            {
                                ratingList.Add(MapEsrbRating(val));
                            }
                            else
                            {
                                ratingList.Add($"{label} {val}");
                            }
                        }
                    }
                }
            }

            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={game.SteamID.Value}";
                var json = await DataCacheService.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty(game.SteamID.Value.ToString(), out var appNode))
                {
                    return;
                }

                if (appNode.TryGetProperty("success", out var successNode) && successNode.ValueKind == JsonValueKind.False)
                {
                    return;
                }

                if (!appNode.TryGetProperty("data", out var data))
                {
                    return;
                }

                // Description
                if (data.TryGetProperty("short_description", out var desc) && desc.ValueKind == JsonValueKind.String)
                {
                    game.Description = desc.GetString();
                }

                // Publisher / Developer
                if (data.TryGetProperty("publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array)
                {
                    game.Publisher = pubs.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? game.Publisher;
                }
                if (data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array)
                {
                    game.Developer = devs.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? game.Developer;
                }

                // Genres
                if (data.TryGetProperty("genres", out var genresNode) && genresNode.ValueKind == JsonValueKind.Array)
                {
                    var genres = genresNode.EnumerateArray()
                        .Select(g => g.TryGetProperty("description", out var descNode) ? descNode.GetString() : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToList();
                    if (genres.Count > 0) game.Genres = genres;
                }

                // Categories (disabled per request)

                await PopulatePriceAsync(game).ConfigureAwait(false);

                // Release date fallback: only set when missing (RAWG preferred)
                if (!game.ReleaseDate.HasValue && data.TryGetProperty("release_date", out var releaseNode) && releaseNode.ValueKind == JsonValueKind.Object)
                {
                    bool comingSoon = releaseNode.TryGetProperty("coming_soon", out var soonNode) && soonNode.ValueKind == JsonValueKind.True;
                    if (!comingSoon && releaseNode.TryGetProperty("date", out var dateNode) && dateNode.ValueKind == JsonValueKind.String)
                    {
                        var dateText = dateNode.GetString();
                        if (!string.IsNullOrWhiteSpace(dateText) && DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
                        {
                            game.ReleaseDate = parsed;
                        }
                    }
                }

                // Reviews summary
                await PopulateReviewsAsync(game).ConfigureAwait(false);

                // Age rating
                var ratings = new List<string>();
                if (data.TryGetProperty("ratings", out var ratingsNode) && ratingsNode.ValueKind == JsonValueKind.Object)
                {
                    AddRating(ratingsNode, "esrb", "ESRB", ratings);
                    AddRating(ratingsNode, "pegi", "PEGI", ratings);
                    AddRating(ratingsNode, "usk", "USK", ratings);
                }
                if (ratings.Count > 0)
                {
                    game.AgeRating = string.Join(", ", ratings);
                }

                // Media (screenshots + movies)
                var media = new List<string>();
                if (data.TryGetProperty("screenshots", out var shotsNode) && shotsNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var shot in shotsNode.EnumerateArray())
                    {
                        if (shot.TryGetProperty("path_full", out var full) && full.ValueKind == JsonValueKind.String)
                        {
                            media.Add(full.GetString()!);
                        }
                    }
                }
                if (data.TryGetProperty("movies", out var moviesNode) && moviesNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var movie in moviesNode.EnumerateArray())
                    {
                        // Prefer mp4 480p; fallback to dash_av1 if mp4 missing
                        if (movie.TryGetProperty("mp4", out var mp4Obj) && mp4Obj.ValueKind == JsonValueKind.Object)
                        {
                            if (mp4Obj.TryGetProperty("480", out var mp4Url) && mp4Url.ValueKind == JsonValueKind.String)
                            {
                                media.Add(mp4Url.GetString()!);
                                continue;
                            }
                        }

                        if (movie.TryGetProperty("dash_av1", out var dash) && dash.ValueKind == JsonValueKind.String)
                        {
                            media.Add(dash.GetString()!);
                        }
                    }
                }
                if (media.Count > 0)
                {
                    game.Media = media;
                }

                // Hero image with fallback to non-2x
                var heroPrimary = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.SteamID.Value}/library_hero.jpg";
                var heroFallback = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.SteamID.Value}/library_hero_2x.jpg";
                game.LibHero = await ResolveAssetUrlAsync(heroPrimary, heroFallback).ConfigureAwait(false) ?? heroPrimary;

                // Logo with fallback to non-2x
                var logoPrimary = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.SteamID.Value}/logo.png";
                var logoFallback = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.SteamID.Value}/logo_2x.png";
                game.LibLogo = await ResolveAssetUrlAsync(logoPrimary, logoFallback).ConfigureAwait(false) ?? logoPrimary;

                // Links
                if (data.TryGetProperty("website", out var site) && site.ValueKind == JsonValueKind.String)
                {
                    game.OfficialWebsiteUrl = site.GetString();
                }
                game.SteamPageUrl = $"https://store.steampowered.com/app/{game.SteamID.Value}";

                game.LastUpdated = DateTime.Now;
            }
            catch
            {
                // Ignore fetch errors; leave existing data
            }

        }

        private static string MapEsrbRating(string rating)
        {
            string normalized = rating.Trim();
            string upper = normalized.ToUpperInvariant();

            return upper switch
            {
                "TEEN" => "ESRB 13+",
                "T" => "ESRB 13+",

                "MATURE" => "ESRB 17+",
                "M" => "ESRB 17+",
                "MATURE 17+" => "ESRB 17+",

                "ADULTS ONLY" => "ESRB 18+",
                "AO" => "ESRB 18+",

                "EVERYONE" => "ESRB Everyone",
                "E" => "ESRB Everyone",

                "EVERYONE 10+" => "ESRB 10+",
                "E10+" => "ESRB 10+",
                "E10" => "ESRB 10+",
                "E 10+" => "ESRB 10+",

                _ => string.IsNullOrWhiteSpace(normalized) ? "Not Rated" : $"ESRB {normalized}"
            };
        }

        private static async Task PopulateReviewsAsync(Game game)
        {
            try
            {
                var url = $"https://store.steampowered.com/appreviews/{game.SteamID}/?json=1&language=all&filter=all&num_per_page=0";
                var json = await DataCacheService.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("query_summary", out var summary) || summary.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                int totalPositive = summary.TryGetProperty("total_positive", out var posProp) && posProp.TryGetInt32(out var posVal) ? posVal : 0;
                int totalReviews = summary.TryGetProperty("total_reviews", out var totProp) && totProp.TryGetInt32(out var totVal) ? totVal : 0;
                string? desc = summary.TryGetProperty("review_score_desc", out var descProp) && descProp.ValueKind == JsonValueKind.String ? descProp.GetString() : null;

                if (totalReviews > 0)
                {
                    double pct = (double)totalPositive / totalReviews * 100.0;
                    game.SteamRating = pct;
                    game.SteamReviewSummary = $"{pct:0}% {desc ?? ""}".Trim();
                    game.SteamReviewTotal = totalReviews;
                }
            }
            catch
            {
                // ignore review fetch errors
            }
        }

        private static async Task PopulatePriceAsync(Game game)
        {
            try
            {
                var url = $"https://steamspy.com/api.php?request=appdetails&appid={game.SteamID}";
                var json = await DataCacheService.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                int priceCents = TryGetInt(root, "price");
                int initialCents = TryGetInt(root, "initialprice");
                int apiDiscount = TryGetInt(root, "discount");

                if (priceCents <= 0 && initialCents <= 0)
                {
                    game.Price = "Free";
                    game.PriceDiscount = null;
                    return;
                }

                if (initialCents <= 0)
                {
                    initialCents = priceCents;
                }

                string priceDisplay = priceCents <= 0 ? "Free" : FormatPrice(priceCents);
                game.Price = priceDisplay;

                if (initialCents == priceCents)
                {
                    game.PriceDiscount = null;
                    return;
                }

                string initialDisplay = FormatPrice(initialCents);

                int percent = apiDiscount >= 0 ? apiDiscount : ComputeDiscountPercent(initialCents, priceCents);
                percent = Math.Clamp(percent, 0, 100);

                game.PriceDiscount = $" ({percent}% off {initialDisplay})";
            }
            catch
            {
                // ignore price fetch errors
            }
        }

        private static int TryGetInt(JsonElement root, string property)
        {
            try
            {
                if (root.TryGetProperty(property, out var node))
                {
                    if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out int intVal))
                    {
                        return intVal;
                    }

                    if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), out int parsed))
                    {
                        return parsed;
                    }
                }
            }
            catch
            {
                // ignore parse issues
            }

            return -1;
        }

        private static int ComputeDiscountPercent(int initialCents, int priceCents)
        {
            if (initialCents <= 0 || priceCents < 0)
            {
                return 0;
            }

            double pct = 1.0 - ((double)priceCents / initialCents);
            return (int)Math.Round(pct * 100, MidpointRounding.AwayFromZero);
        }

        private static string FormatPrice(int cents)
        {
            if (cents < 0)
            {
                return string.Empty;
            }

            return $"${cents / 100.0:0.00}";
        }
    }
}
