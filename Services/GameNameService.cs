using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services
{
    public static class GameNameService
    {
        private const string SteamSearchUrl = "https://steamcommunity.com/actions/SearchApps/";
        private const string SteamDetailsUrl = "https://store.steampowered.com/api/appdetails?appids=";

        private static readonly HttpClient _httpClient = new();
        private static readonly ScannerConfig Config = new();
        private static readonly SemaphoreSlim SteamApiSemaphore = new(Config.MaxConcurrentApiRequests, Config.MaxConcurrentApiRequests);
        private static readonly ConcurrentDictionary<string, CachedSearchEntry> SearchCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private static readonly HashSet<string> DeprioritizedTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "win64", "win32", "x64", "x86", "bin", "binaries", "game", "data", "content",
            "win64-shipping", "win32-shipping", "shipping", "launcher", "bootstrap", "UE4", "UE5", "Unreal Engine", "Engine"
        };

        private static readonly string[] CommonPrefixes = { "setup", "launcher", "client" };
        private static readonly string[] CommonSuffixes = { "setup", "launcher", "client", "game" };
        private static readonly string[] EditionSuffixes =
        {
            "deluxe", "standard", "enhanced", "remastered", "ultimate", "definitive",
            "complete", "goty", "gold", "digital", "edition", "anniversary"
        };

        private static readonly Regex MultiSpaceRegex = new("\\s+", RegexOptions.Compiled);
        private static readonly Regex CamelCaseRegex = new("(?<=[a-z0-9])([A-Z])", RegexOptions.Compiled);
        private static readonly Regex SpecialCharRegex = new("[()\\[\\]{},-:;!?]", RegexOptions.Compiled);

        private static readonly object RateLimitGate = new();
        private static DateTime _lastSteamRequestUtc = DateTime.MinValue;

        static GameNameService()
        {
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodecGameScanner/1.0 (+https://github.com/mkj777/codec)");
            }

            _httpClient.Timeout = TimeSpan.FromMilliseconds(Config.ApiTimeoutMs);
        }

        public record ScannerConfig
        {
            public float HighConfidenceThreshold { get; init; } = 0.90f;
            public float AcceptableConfidenceThreshold { get; init; } = 0.60f;
            public int MaxSearchVariants { get; init; } = 10;
            public int MaxSteamResults { get; init; } = 5;
            public int ApiTimeoutMs { get; init; } = 8000;
            public int MaxConcurrentApiRequests { get; init; } = 2;
            public bool UseCaching { get; init; } = true;
            public int CacheDurationDays { get; init; } = 7;
            public bool EnableFallbackSearch { get; init; } = true;
            public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromMilliseconds(600);
            public int MaxRomanNumeralValue { get; init; } = 20;
        }

        public record LocalGameCandidate
        {
            public required string DetectedName { get; init; }
            public required string FullPath { get; init; }
            public required string ExecutableName { get; init; }
            public string? FileMetadataProductName { get; init; }
            public string? Version { get; init; }
            public string MetadataSource { get; init; } = "folder";
        }

        public record SearchCandidate
        {
            public required string SearchTerm { get; init; }
            public required string NormalizedLocalName { get; init; }
            public required int Priority { get; init; }
            public required float InitialWeight { get; init; }
            public required LocalGameCandidate Source { get; init; }
        }

        public record SteamSearchResult
        {
            [JsonPropertyName("appid")]
            public uint AppId { get; init; }

            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("logo")]
            public string Logo { get; init; } = string.Empty;

            [JsonPropertyName("price")]
            public string Price { get; init; } = string.Empty;

            [JsonPropertyName("img")]
            public string Image { get; init; } = string.Empty;
        }

        public record GameMatch
        {
            public required uint SteamAppId { get; init; }
            public required string SteamName { get; init; }
            public required string LocalPath { get; init; }
            public required float ConfidenceScore { get; init; }
            public required string MatchedSearchTerm { get; init; }
            public required LocalGameCandidate LocalData { get; init; }
        }

        private sealed record CachedSearchEntry(DateTime Timestamp, List<SteamSearchResult> Results);

        private static class NativeMethods
        {
            [DllImport("version.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern int GetFileVersionInfoSize(string lptstrFilename, out int lpdwHandle);

            [DllImport("version.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool GetFileVersionInfo(string lptstrFilename, int dwHandle, int dwLen, IntPtr lpData);

            [DllImport("version.dll", CharSet = CharSet.Unicode)]
            public static extern bool VerQueryValue(IntPtr pBlock, string lpSubBlock, out IntPtr lplpBuffer, out uint puLen);
        }

        private static string? GetVersionInfoValue(string path, string valueKey)
        {
            if (!File.Exists(path)) return null;

            int handle = 0;
            int size = NativeMethods.GetFileVersionInfoSize(path, out handle);
            if (size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.GetFileVersionInfo(path, 0, size, buffer)) return null;

                if (NativeMethods.VerQueryValue(buffer, @"\VarFileInfo\Translation", out IntPtr transPtr, out uint transLen) && transLen > 0)
                {
                    int lang = Marshal.ReadInt16(transPtr);
                    int codePage = Marshal.ReadInt16(transPtr, 2);
                    string subBlock = $"\\StringFileInfo\\{lang:X4}{codePage:X4}\\{valueKey}";

                    if (NativeMethods.VerQueryValue(buffer, subBlock, out IntPtr valuePtr, out uint valueLen) && valueLen > 0)
                    {
                        return Marshal.PtrToStringUni(valuePtr);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return null;
        }

        public static async Task<(int? steamId, int? rawgId)> FindGameIdsAsync(string exePath)
        {
            GameMatch? steamMatch = await ResolveSteamMatchAsync(exePath);
            int? steamId = steamMatch != null ? (int)steamMatch.SteamAppId : null;
            int? rawgId = null;

            if (steamMatch != null)
            {
                Debug.WriteLine($"Using Steam name for RAWG search: {steamMatch.SteamName}");
                rawgId = await FindRawgIdByNameAsync(steamMatch.SteamName, RawgValidationMode.SteamBacked);
            }

            if (!rawgId.HasValue)
            {
                Debug.WriteLine("No Steam ID or RAWG lookup failed, using EXE-based names...");
                rawgId = await FindRawgIdAsync(exePath);
            }

            return (steamId, rawgId);
        }

        private static async Task<GameMatch?> ResolveSteamMatchAsync(string exePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return null;
            }

            int? steamAppId = GetSteamAppIdFromFile(exePath);
            if (steamAppId.HasValue)
            {
                string? steamGameName = await GetSteamGameNameAsync(steamAppId.Value);
                string fallbackName = steamGameName ?? Path.GetFileNameWithoutExtension(exePath);

                return new GameMatch
                {
                    SteamAppId = (uint)steamAppId.Value,
                    SteamName = fallbackName,
                    LocalPath = exePath,
                    ConfidenceScore = 1.0f,
                    MatchedSearchTerm = "steam_appid.txt",
                    LocalData = new LocalGameCandidate
                    {
                        DetectedName = fallbackName,
                        FullPath = exePath,
                        ExecutableName = Path.GetFileNameWithoutExtension(exePath),
                        MetadataSource = "steam_appid",
                        FileMetadataProductName = steamGameName,
                        Version = null
                    }
                };
            }

            List<LocalGameCandidate> candidates = BuildCandidates(exePath);
            if (candidates.Count == 0)
            {
                return null;
            }

            GameMatch? bestMatch = null;

            foreach (LocalGameCandidate candidate in candidates)
            {
                var searchCandidates = GenerateSearchCandidates(candidate)
                    .OrderBy(c => c.Priority)
                    .ThenByDescending(c => c.InitialWeight)
                    .Take(Config.MaxSearchVariants)
                    .ToList();

                foreach (SearchCandidate searchCandidate in searchCandidates)
                {
                    var steamResults = await SearchSteamAsync(searchCandidate.SearchTerm, cancellationToken);
                    if (steamResults.Count == 0)
                    {
                        continue;
                    }

                    foreach (SteamSearchResult result in steamResults.Take(Config.MaxSteamResults))
                    {
                        if (result.AppId == 0 || string.IsNullOrWhiteSpace(result.Name))
                        {
                            continue;
                        }

                        string normalizedSteamName = NormalizeName(result.Name);
                        float score = CalculateMatchScore(searchCandidate.NormalizedLocalName, normalizedSteamName, searchCandidate.Source);
                        if (score <= 0f)
                        {
                            continue;
                        }

                        var match = new GameMatch
                        {
                            SteamAppId = result.AppId,
                            SteamName = result.Name,
                            LocalPath = exePath,
                            ConfidenceScore = score,
                            MatchedSearchTerm = searchCandidate.SearchTerm,
                            LocalData = searchCandidate.Source
                        };

                        if (bestMatch is null || match.ConfidenceScore > bestMatch.ConfidenceScore)
                        {
                            bestMatch = match;
                        }

                        if (match.ConfidenceScore >= Config.HighConfidenceThreshold)
                        {
                            Debug.WriteLine($"  ✓ High-confidence Steam match: {match.SteamName} ({match.SteamAppId}) via '{match.MatchedSearchTerm}' [{match.ConfidenceScore:P}]");
                            return match;
                        }
                    }
                }
            }

            if (bestMatch != null && bestMatch.ConfidenceScore >= Config.AcceptableConfidenceThreshold)
            {
                Debug.WriteLine($"  ✓ Acceptable Steam match: {bestMatch.SteamName} ({bestMatch.SteamAppId}) [{bestMatch.ConfidenceScore:P}]");
                return bestMatch;
            }

            Debug.WriteLine("  ✗ No Steam match reached acceptable confidence threshold");
            return null;
        }

        private static async Task<string?> GetSteamGameNameAsync(int steamId)
        {
            try
            {
                string url = $"{SteamDetailsUrl}{steamId}";
                var response = await _httpClient.GetStringAsync(url);
                using var jsonDoc = JsonDocument.Parse(response);

                if (jsonDoc.RootElement.TryGetProperty(steamId.ToString(), out var appData) &&
                    appData.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    appData.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("name", out var nameProperty))
                {
                    return nameProperty.GetString()?.Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Steam details: {ex.Message}");
            }
            return null;
        }

        private static async Task<int?> FindSteamIdAsync(string exePath)
        {
            GameMatch? match = await ResolveSteamMatchAsync(exePath);
            return match != null ? (int)match.SteamAppId : null;
        }

        private static List<LocalGameCandidate> BuildCandidates(string exePath)
        {
            var candidates = new List<LocalGameCandidate>();

            string executableName = Path.GetFileNameWithoutExtension(exePath);
            string directoryName = Path.GetDirectoryName(exePath) ?? string.Empty;
            string folderName = Directory.Exists(directoryName) ? new DirectoryInfo(directoryName).Name : executableName;

            string? productName = GetVersionInfoValue(exePath, "ProductName");
            string? fileDescription = GetVersionInfoValue(exePath, "FileDescription");
            string? productVersion = GetVersionInfoValue(exePath, "ProductVersion");

            string? muiPath = string.IsNullOrEmpty(productName) && string.IsNullOrEmpty(fileDescription)
                ? FindMuiFile(exePath)
                : null;

            if (!string.IsNullOrEmpty(muiPath))
            {
                productName ??= GetVersionInfoValue(muiPath, "ProductName");
                fileDescription ??= GetVersionInfoValue(muiPath, "FileDescription");
            }

            void AddCandidate(string? name, string source)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                candidates.Add(new LocalGameCandidate
                {
                    DetectedName = name.Trim(),
                    FullPath = exePath,
                    ExecutableName = executableName,
                    FileMetadataProductName = productName,
                    Version = productVersion,
                    MetadataSource = source
                });
            }

            AddCandidate(productName, "file_metadata");
            if (!string.IsNullOrWhiteSpace(fileDescription) && !string.Equals(productName, fileDescription, StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(fileDescription, "file_metadata");
            }

            AddCandidate(folderName, "folder");
            AddCandidate(executableName, "executable");

            return candidates
                .GroupBy(c => $"{c.MetadataSource}|{c.DetectedName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static IEnumerable<SearchCandidate> GenerateSearchCandidates(LocalGameCandidate candidate)
        {
            var (priority, weight) = candidate.MetadataSource.ToLowerInvariant() switch
            {
                "steam_appid" => (0, 1.0f),
                "file_metadata" => (1, 1.0f),
                "registry" => (2, 0.9f),
                "folder" => (3, 0.8f),
                "executable" => (4, 0.7f),
                _ => (5, 0.6f)
            };

            var seenTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seeds = new List<string?>
            {
                candidate.DetectedName,
                candidate.FileMetadataProductName,
                candidate.MetadataSource.Equals("folder", StringComparison.OrdinalIgnoreCase) ? candidate.DetectedName : null,
                candidate.ExecutableName
            };

            foreach (string? seed in seeds)
            {
                string normalized = NormalizeName(seed);
                if (string.IsNullOrWhiteSpace(normalized) || !seenTerms.Add(normalized))
                {
                    continue;
                }

                yield return new SearchCandidate
                {
                    SearchTerm = normalized,
                    NormalizedLocalName = normalized,
                    Priority = priority,
                    InitialWeight = weight,
                    Source = candidate
                };

                foreach (string variant in GenerateProgressiveVariants(normalized))
                {
                    if (!seenTerms.Add(variant)) continue;
                    yield return new SearchCandidate
                    {
                        SearchTerm = variant,
                        NormalizedLocalName = variant,
                        Priority = priority + 1,
                        InitialWeight = MathF.Max(0.4f, weight - 0.1f),
                        Source = candidate
                    };
                }
            }
        }

        private static IEnumerable<string> GenerateProgressiveVariants(string normalized)
        {
            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string withoutEdition = RemoveEditionSuffixes(normalized);
            if (!string.Equals(withoutEdition, normalized, StringComparison.OrdinalIgnoreCase))
            {
                variants.Add(withoutEdition);
            }

            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int len = tokens.Count - 1; len >= Math.Max(1, tokens.Count - 3); len--)
            {
                string shortened = string.Join(' ', tokens.Take(len));
                if (shortened.Length >= 3)
                {
                    variants.Add(shortened);
                }
            }

            foreach (string numericVariant in GenerateNumericVariants(normalized))
            {
                variants.Add(numericVariant);
            }

            return variants;
        }

        private static IEnumerable<string> GenerateNumericVariants(string normalized)
        {
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (TryParseRoman(token, out int number))
                {
                    var clone = (string[])tokens.Clone();
                    clone[i] = number.ToString();
                    yield return string.Join(' ', clone);
                }
                else if (int.TryParse(token, out int numeric) && numeric > 0 && numeric <= Config.MaxRomanNumeralValue)
                {
                    string? roman = ToRoman(numeric);
                    if (!string.IsNullOrEmpty(roman))
                    {
                        var clone = (string[])tokens.Clone();
                        clone[i] = roman.ToLowerInvariant();
                        yield return string.Join(' ', clone);
                    }
                }
            }
        }

        private static string RemoveEditionSuffixes(string name)
        {
            var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (tokens.Count > 1 && EditionSuffixes.Contains(tokens[^1], StringComparer.OrdinalIgnoreCase))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            return string.Join(' ', tokens);
        }

        private static async Task<List<SteamSearchResult>> SearchSteamAsync(string normalizedName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return new List<SteamSearchResult>();
            }

            string cacheKey = normalizedName.ToLowerInvariant();
            if (Config.UseCaching && SearchCache.TryGetValue(cacheKey, out var cached))
            {
                if ((DateTime.UtcNow - cached.Timestamp).TotalDays < Config.CacheDurationDays)
                {
                    return cached.Results;
                }
                SearchCache.TryRemove(cacheKey, out _);
            }

            string url = $"{SteamSearchUrl}{Uri.EscapeDataString(normalizedName)}";

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await SteamApiSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await DelayForRateLimitAsync(cancellationToken);
                        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);

                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            await Task.Delay((int)Math.Pow(2, attempt) * 1000, cancellationToken);
                            continue;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            Debug.WriteLine($"  ✗ Steam search failed ({(int)response.StatusCode}) for '{normalizedName}'");
                            if (!Config.EnableFallbackSearch)
                            {
                                return new List<SteamSearchResult>();
                            }
                            continue;
                        }

                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        var results = await JsonSerializer.DeserializeAsync<List<SteamSearchResult>>(stream, JsonOptions, cancellationToken) ?? new List<SteamSearchResult>();

                        if (Config.UseCaching)
                        {
                            SearchCache[cacheKey] = new CachedSearchEntry(DateTime.UtcNow, results);
                        }

                        return results;
                    }
                    finally
                    {
                        SteamApiSemaphore.Release();
                    }
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine("  ? Steam search timeout, retrying...");
                }
                catch (HttpRequestException ex) when (attempt < 2)
                {
                    Debug.WriteLine($"  ? Steam search transient error: {ex.Message}");
                    await Task.Delay((int)Math.Pow(2, attempt) * 500, cancellationToken);
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine($"  ✗ Steam search parse error: {ex.Message}");
                    return new List<SteamSearchResult>();
                }
            }

            return new List<SteamSearchResult>();
        }

        private static async Task DelayForRateLimitAsync(CancellationToken cancellationToken)
        {
            TimeSpan delay = TimeSpan.Zero;
            lock (RateLimitGate)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastSteamRequestUtc;
                if (elapsed < Config.RateLimitDelay)
                {
                    delay = Config.RateLimitDelay - elapsed;
                }
                _lastSteamRequestUtc = now + delay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        private static float CalculateMatchScore(string localName, string steamResult, LocalGameCandidate source)
        {
            if (string.IsNullOrWhiteSpace(localName) || string.IsNullOrWhiteSpace(steamResult))
            {
                return 0f;
            }

            if (localName.Equals(steamResult, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0f;
            }

            float score = 0f;

            float levenshteinSimilarity = 1f - (float)LevenshteinDistance(localName, steamResult) /
                Math.Max(localName.Length, steamResult.Length);
            score += levenshteinSimilarity * 0.6f;

            float tokenOverlap = CalculateTokenOverlap(localName, steamResult);
            score += tokenOverlap * 0.3f;

            score += source.MetadataSource.ToLowerInvariant() switch
            {
                "file_metadata" => 0.15f,
                "folder" => 0.10f,
                "executable" => 0.05f,
                "registry" => 0.12f,
                _ => 0f
            };

            float lengthPenalty = Math.Abs(localName.Length - steamResult.Length) /
                                   (float)Math.Max(localName.Length, steamResult.Length);
            score -= lengthPenalty * 0.1f;

            return Math.Clamp(score, 0f, 1f);
        }

        private static float CalculateTokenOverlap(string s1, string s2)
        {
            var tokens1 = s1.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tokens2 = s2.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (tokens1.Length == 0 || tokens2.Length == 0)
            {
                return 0f;
            }

            var intersection = tokens1.Intersect(tokens2, StringComparer.OrdinalIgnoreCase).Count();
            var union = tokens1.Union(tokens2, StringComparer.OrdinalIgnoreCase).Count();

            return union == 0 ? 0f : intersection / (float)union;
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
            if (string.IsNullOrEmpty(target)) return source.Length;

            var d = new int[source.Length + 1, target.Length + 1];
            for (int i = 0; i <= source.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= target.Length; j++) d[0, j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[source.Length, target.Length];
        }

        private static string NormalizeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string value = raw.Trim();
            value = value.Replace('_', ' ').Replace('+', ' ');
            value = CamelCaseRegex.Replace(value, " $1");
            value = RemoveFileExtension(value);
            value = SpecialCharRegex.Replace(value, " ");
            value = value.Replace("/", " ");
            value = SanitizeUmlauts(value);
            value = TrimAffixes(value);
            value = value.ToLowerInvariant();
            value = RemoveEditionSuffixes(value);
            value = MultiSpaceRegex.Replace(value, " ").Trim();
            return value;
        }

        private static string RemoveFileExtension(string value)
        {
            var extensions = new[] { ".exe", ".msi" };
            foreach (string ext in extensions)
            {
                if (value.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return value[..^ext.Length];
                }
            }
            return value;
        }

        private static string TrimAffixes(string value)
        {
            foreach (string prefix in CommonPrefixes)
            {
                value = Regex.Replace(value, $"^{prefix}\\s+", string.Empty, RegexOptions.IgnoreCase);
            }

            foreach (string suffix in CommonSuffixes)
            {
                value = Regex.Replace(value, $"\\s+{suffix}$", string.Empty, RegexOptions.IgnoreCase);
            }

            return value;
        }

        private static string SanitizeUmlauts(string value)
        {
            return value
                .Replace("ä", "ae", StringComparison.OrdinalIgnoreCase)
                .Replace("ö", "oe", StringComparison.OrdinalIgnoreCase)
                .Replace("ü", "ue", StringComparison.OrdinalIgnoreCase)
                .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase)
                .Replace("á", "a", StringComparison.OrdinalIgnoreCase)
                .Replace("é", "e", StringComparison.OrdinalIgnoreCase)
                .Replace("í", "i", StringComparison.OrdinalIgnoreCase)
                .Replace("ó", "o", StringComparison.OrdinalIgnoreCase)
                .Replace("ú", "u", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeRawName(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '\'')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append(' ');
                }
            }
            return builder.ToString();
        }

        private static string? FindMuiFile(string exePath)
        {
            string? dir = Path.GetDirectoryName(exePath);
            if (dir == null) return null;

            string baseName = Path.GetFileName(exePath);
            var culture = System.Globalization.CultureInfo.CurrentUICulture;

            string specificCulturePath = Path.Combine(dir, culture.Name, $"{baseName}.mui");
            if (File.Exists(specificCulturePath)) return specificCulturePath;

            if (culture.Parent != null && !string.IsNullOrEmpty(culture.Parent.Name))
            {
                string parentCulturePath = Path.Combine(dir, culture.Parent.Name, $"{baseName}.mui");
                if (File.Exists(parentCulturePath)) return parentCulturePath;
            }

            return null;
        }

        public static string? GetBestName(string exePath)
        {
            return GetPrioritizedNames(exePath).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(exePath);
        }

        private static List<string> GetPrioritizedNames(string exePath)
        {
            string? productName = GetVersionInfoValue(exePath, "ProductName");
            string? fileDescription = GetVersionInfoValue(exePath, "FileDescription");

            if (string.IsNullOrEmpty(productName) && string.IsNullOrEmpty(fileDescription))
            {
                string? muiPath = FindMuiFile(exePath);
                if (muiPath != null)
                {
                    productName = GetVersionInfoValue(muiPath, "ProductName");
                    fileDescription = GetVersionInfoValue(muiPath, "FileDescription");
                }
            }

            var names = new List<string?>
            {
                productName,
                fileDescription,
                new DirectoryInfo(Path.GetDirectoryName(exePath)!).Name,
                Path.GetFileNameWithoutExtension(exePath)
            };

            var distinctNames = names.Where(n => !string.IsNullOrWhiteSpace(n))
                                     .Select(n => n!.Trim())
                                     .Where(n => n.Length >= 3)
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(n => DeprioritizedTerms.Any(term => n.Contains(term, StringComparison.OrdinalIgnoreCase)))
                                     .ToList();

            Debug.WriteLine("Name priorities determined:");
            distinctNames.ForEach(name => Debug.WriteLine($"  - {name}"));

            return distinctNames;
        }

        private static int? GetSteamAppIdFromFile(string exePath)
        {
            try
            {
                string? gameDir = Path.GetDirectoryName(exePath);
                if (gameDir == null) return null;
                string filePath = Path.Combine(gameDir, "steam_appid.txt");
                if (File.Exists(filePath))
                {
                    if (int.TryParse(File.ReadAllText(filePath).Trim(), out int id))
                    {
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading steam_appid.txt: {ex.Message}");
            }
            return null;
        }

        private static async Task<int?> FindRawgIdAsync(string exePath)
        {
            if (GameContentHeuristics.PathMatchesUtility(exePath))
            {
                return null;
            }

            string? bestName = GetBestName(exePath);
            if (string.IsNullOrWhiteSpace(bestName) || GameContentHeuristics.NameMatchesUtility(bestName))
            {
                return null;
            }

            return await FindRawgIdByNameAsync(bestName, RawgValidationMode.Strict);
        }

        public static async Task<int?> FindRawgIdByNameAsync(string gameName, RawgValidationMode mode = RawgValidationMode.Strict)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            return await GameDetailsService.ValidateGameAsync(gameName, mode);
        }

        private static bool TryParseRoman(string token, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;

            token = token.ToUpperInvariant();
            var map = new Dictionary<char, int>
            {
                ['I'] = 1,
                ['V'] = 5,
                ['X'] = 10,
                ['L'] = 50,
                ['C'] = 100,
                ['D'] = 500,
                ['M'] = 1000
            };

            int total = 0;
            int prev = 0;
            foreach (char c in token)
            {
                if (!map.TryGetValue(c, out int current))
                {
                    return false;
                }

                if (current > prev && prev != 0)
                {
                    total += current - 2 * prev;
                }
                else
                {
                    total += current;
                }
                prev = current;
            }

            value = total;
            return true;
        }

        private static string? ToRoman(int number)
        {
            if (number <= 0 || number > 3999) return null;

            var numerals = new (int Value, string Symbol)[]
            {
                (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
                (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
                (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
            };

            var builder = new StringBuilder();
            foreach (var (value, symbol) in numerals)
            {
                while (number >= value)
                {
                    builder.Append(symbol);
                    number -= value;
                }
            }

            return builder.ToString();
        }
    }
}
