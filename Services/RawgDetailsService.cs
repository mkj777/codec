using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codec.Models;

namespace Codec.Services
{
    public static class RawgDetailsService
    {
        private const string DetailsEndpoint = "https://codec-api-proxy.vercel.app/api/rawg/details?id=";
        private static readonly HttpClient Http = new();

        public static async Task PopulateAsync(Game game)
        {
            if (game == null || !game.RawgID.HasValue)
            {
                return;
            }

            try
            {
                string url = DetailsEndpoint + game.RawgID.Value;
                string json = await DataCacheService.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                // Slug + link
                string? slug = GetString(root, "slug");
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    game.RawgSlug = slug;
                    game.RawgUrl = $"https://rawg.io/games/{slug}";
                }

                // Release date (required for all games)
                string? release = GetString(root, "released");
                if (!string.IsNullOrWhiteSpace(release) && DateTime.TryParse(release, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var releaseDate))
                {
                    game.ReleaseDate = releaseDate;
                }

                // Website
                string? website = GetString(root, "website");
                if (!string.IsNullOrWhiteSpace(website) && ShouldOverwrite(game.OfficialWebsiteUrl, game.SteamID))
                {
                    game.OfficialWebsiteUrl = website;
                }

                // Developer / Publisher
                string? developer = GetFirstName(root, "developers");
                if (!string.IsNullOrWhiteSpace(developer) && ShouldOverwrite(game.Developer, game.SteamID))
                {
                    game.Developer = developer;
                }

                string? publisher = GetFirstName(root, "publishers");
                if (!string.IsNullOrWhiteSpace(publisher) && ShouldOverwrite(game.Publisher, game.SteamID))
                {
                    game.Publisher = publisher;
                }

                // Description
                string? description = GetString(root, "description_raw") ?? GetString(root, "description");
                if (!string.IsNullOrWhiteSpace(description) && ShouldOverwrite(game.Description, game.SteamID))
                {
                    string cleaned = StripHtml(description);
                    game.Description = TruncateWithEllipsis(cleaned, 250);
                }

                // Genres
                var genres = GetNameList(root, "genres");
                if (genres.Count > 0 && (game.Genres == null || game.Genres.Count == 0 || !game.SteamID.HasValue))
                {
                    game.Genres = genres;
                }

                // Platforms
                var platforms = GetNameList(root, "platforms", childProperty: "platform");
                if (platforms.Count > 0 && (game.Platforms == null || game.Platforms.Count == 0 || !game.SteamID.HasValue))
                {
                    game.Platforms = platforms;
                }

                // Age rating with ESRB mapping; fill even for Steam games if Steam provided none
                if (root.TryGetProperty("esrb_rating", out var esrb) && esrb.ValueKind == JsonValueKind.Object)
                {
                    string? rating = GetString(esrb, "name");
                    if (!string.IsNullOrWhiteSpace(rating) && (string.IsNullOrWhiteSpace(game.AgeRating) || ShouldOverwrite(game.AgeRating, game.SteamID)))
                    {
                        game.AgeRating = MapEsrbRating(rating);
                    }
                }

                // Hero image with RAWG fallbacks. For non-Steam games, use the RAWG URL directly to avoid crop failures; for Steam-backed games, prefer cropped ratio.
                string? heroSource = GetString(root, "background_image")
                                   ?? GetString(root, "background_image_additional")
                                   ?? GetFirstScreenshot(root);

                string? hero = null;

                if (!game.SteamID.HasValue)
                {
                    hero = heroSource; // use RAWG directly for non-Steam games
                }
                else
                {
                    hero = NormalizeHero(heroSource) ?? heroSource;
                }

                if (!string.IsNullOrWhiteSpace(hero) && (IsPlaceholder(game.LibHero) || !game.SteamID.HasValue))
                {
                    game.LibHero = hero;
                }

                game.LastUpdated = DateTime.Now;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RAWG details fetch failed for {game?.Name}: {ex.Message}");
            }
        }

        private static string? GetString(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }
            }
            return null;
        }

        private static string? GetFirstName(JsonElement root, string arrayProperty)
        {
            if (root.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    string? name = GetString(item, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }
            return null;
        }

        private static List<string> GetNameList(JsonElement root, string arrayProperty, string childProperty = "")
        {
            var list = new List<string>();
            if (root.TryGetProperty(arrayProperty, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    JsonElement target = string.IsNullOrWhiteSpace(childProperty) ? item : item.TryGetProperty(childProperty, out var child) ? child : item;
                    string? name = GetString(target, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add(name);
                    }
                }
            }
            return list;
        }

        private static string StripHtml(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            string withoutTags = Regex.Replace(value, "<[^>]+>", " ");
            return Regex.Replace(withoutTags, "\\s+", " ").Trim();
        }

        private static string MapEsrbRating(string rating)
        {
            string normalized = rating.Trim();

            return normalized switch
            {
                "Teen" => "ESRB 13+",
                "Mature" => "ESRB 17+",
                "Everyone" => "ESRB Everyone",
                "Everyone 10+" => "ESRB 10+",
                "E10+" => "ESRB 10+",
                _ => string.IsNullOrWhiteSpace(normalized) ? "Not Rated" : $"ESRB {normalized}"
            };
        }

        private static string? GetFirstScreenshot(JsonElement root)
        {
            if (root.TryGetProperty("short_screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
            {
                foreach (var shot in shots.EnumerateArray())
                {
                    string? image = GetString(shot, "image") ?? GetString(shot, "url");
                    if (!string.IsNullOrWhiteSpace(image))
                    {
                        return image;
                    }
                }
            }

            return null;
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

        private static bool IsPlaceholder(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return true;
            }

            return url.Contains("placehold.co", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldOverwrite(string? existing, int? steamId)
        {
            if (!steamId.HasValue)
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(existing);
        }

        private static string? NormalizeHero(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return url;
            }

            // Avoid re-cropping if already cropped
            if (parsed.AbsolutePath.Contains("/media/crop/", StringComparison.OrdinalIgnoreCase))
            {
                return parsed.ToString();
            }

            string path = parsed.AbsolutePath;
            int mediaIndex = path.IndexOf("/media/", StringComparison.OrdinalIgnoreCase);
            if (mediaIndex < 0)
            {
                return url;
            }

            string suffix = path.Substring(mediaIndex + "/media/".Length).TrimStart('/');
            string croppedPath = $"/media/crop/1920/620/{suffix}";

            var builder = new UriBuilder(parsed)
            {
                Path = croppedPath,
                Query = string.Empty
            };

            return builder.Uri.ToString();
        }
    }
}
