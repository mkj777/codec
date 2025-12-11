using System;
using System.Collections.Generic;
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
                            ratingList.Add($"{label} {val}");
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

                // Price (disabled for now)

                // Release date (disabled per request)

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
    }
}
