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
                var json = await Http.GetStringAsync(url);
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

                // Price
                if (data.TryGetProperty("price_overview", out var priceNode) && priceNode.ValueKind == JsonValueKind.Object)
                {
                    if (priceNode.TryGetProperty("final_formatted", out var finalFmt) && finalFmt.ValueKind == JsonValueKind.String)
                    {
                        game.Price = finalFmt.GetString();
                    }
                }

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

                // Hero image
                var heroUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.SteamID.Value}/library_hero_2x.jpg";
                game.LibHero = heroUrl;

                // Logo
                var logoUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{game.SteamID.Value}/logo_2x.png";
                game.LibLogo = logoUrl;

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
