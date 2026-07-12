using Codec.Services.Scanning;
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

namespace Codec.Services.Resolving
{
    public class GameNameService
    {
        private const string SteamSearchUrl = "https://steamcommunity.com/actions/SearchApps/";
        private const string SteamDetailsUrl = "https://store.steampowered.com/api/appdetails?appids=";

        private readonly GameDetailsService _gameDetails;
        private readonly HttpClient _httpClient = new();
        private readonly ScannerConfig Config = new();
        private readonly SemaphoreSlim SteamApiSemaphore;
        private readonly ConcurrentDictionary<string, CachedSearchEntry> SearchCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private readonly HashSet<string> DeprioritizedTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "win64", "win32", "x64", "x86", "bin", "binaries", "game", "data", "content",
            "win64-shipping", "win32-shipping", "shipping", "launcher", "bootstrap", "UE4", "UE5", "Unreal Engine", "Engine"
        };

        // PE metadata names that identify the engine/middleware, not the game itself.
        // These must never be used as name candidates for Steam/RAWG matching.
        private static readonly HashSet<string> MetadataNameBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "Unreal Engine", "UnrealGame", "Unreal",
            "Unity", "Unity Player",
            "GameMaker Studio", "GameMaker",
            "Godot Engine", "Godot",
            "CryEngine", "CryGame",
            "Win64", "Win32", "x64", "x86",
            "Engine", "Game", "Launcher", "Bootstrap",
        };

        private readonly string[] CommonPrefixes = { "setup", "launcher", "client" };
        private readonly string[] CommonSuffixes = { "setup", "launcher", "client", "game" };
        private readonly string[] EditionSuffixes =
        {
            "deluxe", "standard", "enhanced", "remastered", "ultimate", "definitive",
            "complete", "goty", "gold", "digital", "edition", "anniversary"
        };

        private readonly Regex MultiSpaceRegex = new("\\s+", RegexOptions.Compiled);
        private readonly Regex CamelCaseRegex = new("(?<=[a-z0-9])([A-Z])", RegexOptions.Compiled);
        private readonly Regex SpecialCharRegex = new("[()\\[\\]{},:;!?-]", RegexOptions.Compiled);
        private readonly Regex TrademarkRegex = new(@"\(TM\)|\(R\)|™|®|©", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly Regex BareTrademarkTokenRegex = new(@"\btm\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly object RateLimitGate = new();
        private DateTime _lastSteamRequestUtc = DateTime.MinValue;

        public GameNameService(GameDetailsService gameDetails, int maxConcurrentApiRequests = 3)
        {
            _gameDetails = gameDetails;
            int concurrency = Math.Max(1, maxConcurrentApiRequests);
            SteamApiSemaphore = new SemaphoreSlim(concurrency, concurrency);

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
            public int MaxSteamResults { get; init; } = 15;
            public int ApiTimeoutMs { get; init; } = 8000;
            public int MaxConcurrentApiRequests { get; init; } = 3;
            public bool UseCaching { get; init; } = true;
            public int CacheDurationDays { get; init; } = 7;
            public bool EnableFallbackSearch { get; init; } = true;
            public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromMilliseconds(200);
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
            public int? CopyrightYear { get; init; }
            public IReadOnlySet<int> CopyrightYears { get; init; } = new HashSet<int>();
            public IReadOnlySet<int> ExpectedSeriesNumbers { get; init; } = new HashSet<int>();
        }

        public sealed record ExeCopyrightInfo(string? Text, IReadOnlySet<int> Years, string Source)
        {
            public static ExeCopyrightInfo Empty { get; } = new(null, new HashSet<int>(), "none");
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

        public enum MatchMethod
        {
            SteamAppIdFile,  // AppID read from steam_appid.txt — no string comparison
            FuzzySearch      // similarity score from Levenshtein + token overlap
        }

        public record GameMatch
        {
            public required uint SteamAppId { get; init; }
            public required string SteamName { get; init; }
            public required string LocalPath { get; init; }
            public required float ConfidenceScore { get; init; }
            public required string MatchedSearchTerm { get; init; }
            public required LocalGameCandidate LocalData { get; init; }
            public required MatchMethod Method { get; init; }
            public int? SteamReleaseYear { get; init; }
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

        private string? GetVersionInfoValue(string path, string valueKey)
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

        public int? TryGetExeCopyrightYear(string exePath)
        {
            var years = TryGetExeCopyrightYears(exePath);
            return years.Count > 0 ? years.Max() : null;
        }

        public IReadOnlySet<int> TryGetExeCopyrightYears(string exePath)
        {
            return TryGetExeCopyrightInfo(exePath).Years;
        }

        public ExeCopyrightInfo TryGetExeCopyrightInfo(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return ExeCopyrightInfo.Empty;
            }

            string? legalCopyright = CleanCopyrightText(GetVersionInfoValue(exePath, "LegalCopyright"));
            if (!string.IsNullOrWhiteSpace(legalCopyright))
            {
                return BuildCopyrightInfo(legalCopyright, "version-resource");
            }

            legalCopyright = CleanCopyrightText(TryGetFileVersionCopyright(exePath));
            if (!string.IsNullOrWhiteSpace(legalCopyright))
            {
                return BuildCopyrightInfo(legalCopyright, "file-version-info");
            }

            string? muiPath = FindMuiFile(exePath);
            if (!string.IsNullOrEmpty(muiPath))
            {
                legalCopyright = CleanCopyrightText(GetVersionInfoValue(muiPath, "LegalCopyright"));
                if (!string.IsNullOrWhiteSpace(legalCopyright))
                {
                    return BuildCopyrightInfo(legalCopyright, "mui-version-resource");
                }

                legalCopyright = CleanCopyrightText(TryGetFileVersionCopyright(muiPath));
                if (!string.IsNullOrWhiteSpace(legalCopyright))
                {
                    return BuildCopyrightInfo(legalCopyright, "mui-file-version-info");
                }
            }

            legalCopyright = TryFindCopyrightStringInBinary(exePath);
            if (!string.IsNullOrWhiteSpace(legalCopyright))
            {
                return BuildCopyrightInfo(legalCopyright, "binary-string-scan");
            }

            return ExeCopyrightInfo.Empty;
        }

        private static ExeCopyrightInfo BuildCopyrightInfo(string text, string source) =>
            MiddlewareCopyrightAuthors.Any(author => text.Contains(author, StringComparison.OrdinalIgnoreCase))
                ? ExeCopyrightInfo.Empty
                : new(text, ExtractCopyrightYears(text), source);

        private static string? TryGetFileVersionCopyright(string path)
        {
            try { return FileVersionInfo.GetVersionInfo(path).LegalCopyright; }
            catch { return null; }
        }

        private static string? TryFindCopyrightStringInBinary(string exePath)
        {
            const long maxScanBytes = 32L * 1024 * 1024;

            try
            {
                var file = new FileInfo(exePath);
                if (!file.Exists || file.Length > maxScanBytes)
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(exePath);
                return FindCopyrightSnippet(Encoding.Latin1.GetString(bytes))
                    ?? FindCopyrightSnippet(Encoding.Unicode.GetString(bytes));
            }
            catch
            {
                return null;
            }
        }

        private static readonly string[] MiddlewareCopyrightAuthors =
        {
            "jean-loup gailly", "mark adler",
            "glenn randers-pehrson",
            "the openssl project", "openssl software",
            "sam leffler", "silicon graphics",
            "the freetype project",
            "the icu project", "international business machines",
            "the chromium authors",
            "skia",
            "unity technologies",
            "jordan russell", "inno setup"
        };

        private static string? FindCopyrightSnippet(string value)
        {
            var match = Regex.Match(
                value,
                @"(?i)(copyright|\(c\)|\u00a9).{0,160}?\b(19[7-9]\d|20\d{2})\b.{0,120}");

            if (!match.Success)
            {
                return null;
            }

            string? cleaned = CleanCopyrightText(match.Value);
            if (cleaned is null)
            {
                return null;
            }

            if (MiddlewareCopyrightAuthors.Any(author =>
                cleaned.Contains(author, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return cleaned;
        }

        private static string? CleanCopyrightText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string cleaned = Regex.Replace(value, @"[\u0000-\u001F]+", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        public static IReadOnlySet<int> ExtractCopyrightYears(string? copyrightText)
        {
            if (string.IsNullOrWhiteSpace(copyrightText))
            {
                return new HashSet<int>();
            }

            int maxReasonableYear = DateTime.UtcNow.Year + 1;
            return Regex.Matches(copyrightText, @"\b(19[7-9]\d|20\d{2})\b")
                .Select(match => int.Parse(match.Value))
                .Where(year => year <= maxReasonableYear)
                .ToHashSet();
        }

        public static bool ReleaseYearMatchesCopyrightYears(IReadOnlySet<int>? copyrightYears, int? releaseYear)
        {
            if (copyrightYears is null || copyrightYears.Count == 0)
            {
                return true;
            }

            return releaseYear.HasValue && copyrightYears.Contains(releaseYear.Value);
        }

        private static string FormatYears(IReadOnlySet<int>? years)
        {
            if (years is null || years.Count == 0)
            {
                return "?";
            }

            return string.Join("/", years.Order());
        }

        public Task<int?> FindRawgIdBySteamIdAsync(int steamId) => _gameDetails.FindRawgIdBySteamIdAsync(steamId);

        public async Task<bool> SteamAppMatchesNameAsync(int steamId, string nameHint)
            => await SteamAppMatchesLocalGameAsync(steamId, nameHint, executablePath: null).ConfigureAwait(false);

        public async Task<bool> SteamAppMatchesLocalGameAsync(int steamId, string nameHint, string? executablePath)
        {
            var (matches, _) = await TrySteamAppMatchLocalGameAsync(steamId, nameHint, executablePath).ConfigureAwait(false);
            return matches;
        }

        /// <summary>
        /// Same as <see cref="SteamAppMatchesLocalGameAsync"/> but also returns the Steam app name
        /// that was checked, so callers can log the rejected name without a second API call.
        /// </summary>
        public async Task<(bool Matches, string? SteamName)> TrySteamAppMatchLocalGameAsync(int steamId, string nameHint, string? executablePath)
        {
            if (steamId <= 0 || string.IsNullOrWhiteSpace(nameHint))
            {
                return (false, null);
            }

            var appSummary = await GetSteamAppSummaryAsync(steamId);
            if (string.IsNullOrWhiteSpace(appSummary.Name) || !SteamNameMatchesLocalName(nameHint, appSummary.Name))
            {
                return (false, appSummary.Name);
            }

            return (true, appSummary.Name);
        }

        public bool SteamNameMatchesLocalName(string localName, string steamName)
        {
            string normalizedLocal = NormalizeName(localName);
            string normalizedSteam = NormalizeName(steamName);
            var localSeriesNumbers = ExtractSeriesNumbers(normalizedLocal);
            var steamSeriesNumbers = ExtractSeriesNumbers(normalizedSteam);
            if (!SeriesNumbersMatch(localSeriesNumbers, steamSeriesNumbers))
            {
                return false;
            }

            var candidate = new LocalGameCandidate
            {
                DetectedName = localName,
                FullPath = string.Empty,
                ExecutableName = string.Empty,
                MetadataSource = "name_validation",
                ExpectedSeriesNumbers = localSeriesNumbers
            };

            if (CalculateMatchScore(normalizedLocal, normalizedSteam, candidate) >= Config.AcceptableConfidenceThreshold)
            {
                return true;
            }

            // Local folder often carries trailing edition tokens (HD, Remaster, etc.) that
            // Steam drops from its canonical title — e.g. local "Resident Evil HD Remaster"
            // vs Steam appdetails name "Resident Evil" (appid 304240).
            string strippedLocal = StripTrailingEditionTokens(normalizedLocal);
            if (!string.Equals(strippedLocal, normalizedLocal, StringComparison.Ordinal) &&
                strippedLocal.Length > 0 &&
                CalculateMatchScore(strippedLocal, normalizedSteam, candidate) >= Config.AcceptableConfidenceThreshold)
            {
                return true;
            }

            // Capcom-style "<English> / <JP> <shared edition suffix>" titles
            // (e.g. "Resident Evil / biohazard HD REMASTER" for local "Resident Evil HD Remaster").
            // Try recombining the primary segment with any trailing edition tokens.
            foreach (string altSteam in EnumerateAltTitleVariants(steamName))
            {
                if (CalculateMatchScore(normalizedLocal, NormalizeName(altSteam), candidate) >= Config.AcceptableConfidenceThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        private static string StripTrailingEditionTokens(string normalizedName)
        {
            var tokens = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (tokens.Count > 1 && EditionSuffixTokens.Contains(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            return string.Join(' ', tokens);
        }

        private static readonly HashSet<string> EditionSuffixTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "hd", "remaster", "remastered", "edition", "definitive", "complete",
            "anniversary", "deluxe", "ultimate", "gold", "goty", "enhanced",
            "redux"
        };

        private static readonly string[] BundleMarkers =
            { "bundle", "pack", "collection", "anthology", "megapack", "compilation" };

        private static IEnumerable<string> EnumerateAltTitleVariants(string steamName)
        {
            int sepIndex = steamName.IndexOfAny(new[] { '/', '|' });
            if (sepIndex < 0) yield break;

            if (BundleMarkers.Any(m => steamName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                yield break;

            string primary = steamName[..sepIndex].Trim();
            string rest = steamName[(sepIndex + 1)..].Trim();
            if (primary.Length == 0 || rest.Length == 0) yield break;

            yield return primary;
            yield return rest;

            string[] restTokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int trailingStart = restTokens.Length;
            for (int i = restTokens.Length - 1; i >= 0; i--)
            {
                if (EditionSuffixTokens.Contains(restTokens[i]))
                    trailingStart = i;
                else
                    break;
            }
            if (trailingStart > 0 && trailingStart < restTokens.Length)
            {
                string suffix = string.Join(' ', restTokens, trailingStart, restTokens.Length - trailingStart);
                yield return $"{primary} {suffix}";
            }
        }

        public async Task<(int? steamId, string? steamName)> FindGameIdsAsync(string exePath, string? nameHint = null, bool skipFuzzySearch = false)
        {
            GameMatch? steamMatch = await ResolveSteamMatchAsync(exePath, nameHint: nameHint, skipFuzzySearch: skipFuzzySearch);

            // SteamAppIdFile is authoritative — no fuzzy threshold applies.
            // FuzzySearch requires high confidence to avoid false positives.
            bool isHighConfidence = steamMatch != null &&
                (steamMatch.Method == MatchMethod.SteamAppIdFile ||
                 steamMatch.ConfidenceScore >= Config.HighConfidenceThreshold);
            int? steamId = isHighConfidence ? (int)steamMatch!.SteamAppId : null;
            string? steamName = isHighConfidence ? steamMatch!.SteamName : null;

            return (steamId, steamName);
        }

        public async Task<(int? steamId, string? steamName)> FindGameIdsByNameAsync(string gameName, CancellationToken cancellationToken = default)
        {
            GameMatch? steamMatch = await ResolveSteamMatchFromCandidatesAsync(BuildNameOnlyCandidates(gameName), cancellationToken);

            bool isHighConfidence = steamMatch != null &&
                steamMatch.ConfidenceScore >= Config.HighConfidenceThreshold;
            int? steamId = isHighConfidence ? (int)steamMatch!.SteamAppId : null;
            string? steamName = isHighConfidence ? steamMatch!.SteamName : null;

            return (steamId, steamName);
        }

        private async Task<GameMatch?> ResolveSteamMatchAsync(string exePath, CancellationToken cancellationToken = default, string? nameHint = null, bool skipFuzzySearch = false)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return null;
            }

            int? steamAppId = GetSteamAppIdFromFile(exePath);
            if (steamAppId.HasValue)
            {
                SteamAppSummary appSummary = await GetSteamAppSummaryAsync(steamAppId.Value).ConfigureAwait(false);
                string fallbackName = appSummary.Name ?? Path.GetFileNameWithoutExtension(exePath);

                return new GameMatch
                {
                    SteamAppId = (uint)steamAppId.Value,
                    SteamName = fallbackName,
                    LocalPath = exePath,
                    ConfidenceScore = 1.0f,
                    MatchedSearchTerm = "steam_appid.txt",
                    Method = MatchMethod.SteamAppIdFile,
                    SteamReleaseYear = appSummary.ReleaseYear,
                    LocalData = new LocalGameCandidate
                    {
                        DetectedName = fallbackName,
                        FullPath = exePath,
                        ExecutableName = Path.GetFileNameWithoutExtension(exePath),
                        MetadataSource = "steam_appid",
                        FileMetadataProductName = appSummary.Name,
                        Version = null
                    }
                };
            }

            if (skipFuzzySearch)
                return null;

            List<LocalGameCandidate> candidates = BuildCandidates(exePath, nameHint);
            return await ResolveSteamMatchFromCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
        }

        private async Task<GameMatch?> ResolveSteamMatchFromCandidatesAsync(List<LocalGameCandidate> candidates, CancellationToken cancellationToken)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var possibleMatches = new Dictionary<uint, GameMatch>();

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

                    foreach (var searchMatch in SelectSteamSearchMatches(steamResults, searchCandidate))
                    {
                        if (!possibleMatches.TryGetValue(searchMatch.SteamAppId, out var existingMatch) ||
                            searchMatch.ConfidenceScore > existingMatch.ConfidenceScore)
                        {
                            possibleMatches[searchMatch.SteamAppId] = searchMatch;
                        }

                        if (searchMatch.ConfidenceScore >= Config.HighConfidenceThreshold)
                        {
                            Debug.WriteLine($"  ✓ High-confidence Steam match: {searchMatch.SteamName} ({searchMatch.SteamAppId}) via '{searchMatch.MatchedSearchTerm}' [{searchMatch.ConfidenceScore:P0}]");
                        }
                    }
                }
            }

            var validatedMatches = new List<GameMatch>();
            foreach (var candidateMatch in possibleMatches.Values
                .Where(match => match.ConfidenceScore >= Config.AcceptableConfidenceThreshold)
                .OrderByDescending(match => match.ConfidenceScore)
                .Take(6))
            {
                var validatedMatch = await ValidateSteamMatchAgainstAppDetailsAsync(candidateMatch).ConfigureAwait(false);
                if (validatedMatch is null)
                {
                    continue;
                }

                string confidence = validatedMatch.ConfidenceScore >= Config.HighConfidenceThreshold ? "High-confidence" : "Acceptable";
                Debug.WriteLine($"  ✓ {confidence} Steam match: {validatedMatch.SteamName} ({validatedMatch.SteamAppId}) [{validatedMatch.ConfidenceScore:P0}]");
                validatedMatches.Add(validatedMatch);
            }

            GameMatch? bestMatch = SelectBestSteamMatch(validatedMatches);
            if (bestMatch is not null)
            {
                Debug.WriteLine($"  ✓ Selected Steam match: {bestMatch.SteamName} ({bestMatch.SteamAppId}) release={bestMatch.SteamReleaseYear?.ToString() ?? "?"} [{bestMatch.ConfidenceScore:P0}]");
                return bestMatch;
            }

            Debug.WriteLine("  ✗ No Steam match reached acceptable confidence threshold");
            return null;
        }

        private static GameMatch? SelectBestSteamMatch(IEnumerable<GameMatch> matches)
            => matches
                .OrderByDescending(match => match.ConfidenceScore)
                .ThenByDescending(match => match.SteamReleaseYear ?? 0)
                .ThenBy(match => match.SteamAppId)
                .FirstOrDefault();

        private async Task<GameMatch?> ValidateSteamMatchAgainstAppDetailsAsync(GameMatch match)
        {
            // The SearchApps API can return a truncated/shortened name (e.g. "SimCity" for
            // "SimCity™ 4 Deluxe Edition", appid 24780). Re-score against the authoritative
            // appdetails name so the numeric-series penalty and token checks fire on the real title.
            var appSummary = await GetSteamAppSummaryAsync((int)match.SteamAppId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(appSummary.Name))
            {
                return match;
            }

            string normalizedAuth = NormalizeName(appSummary.Name);
            string normalizedLocal = NormalizeName(match.LocalData.DetectedName);
            float authScore = CalculateMatchScore(normalizedLocal, normalizedAuth, match.LocalData);
            if (authScore < Config.AcceptableConfidenceThreshold)
            {
                Debug.WriteLine($"  ✗ Rejected: auth name '{appSummary.Name}' rescored {authScore:P0} < threshold (search API returned '{match.SteamName}')");
                return null;
            }

            return match with
            {
                SteamName = appSummary.Name,
                ConfidenceScore = authScore,
                SteamReleaseYear = appSummary.ReleaseYear
            };
        }

        private IReadOnlyList<GameMatch> SelectSteamSearchMatches(IReadOnlyList<SteamSearchResult> steamResults, SearchCandidate searchCandidate)
        {
            var matches = new List<GameMatch>();

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
                    LocalPath = searchCandidate.Source.FullPath,
                    ConfidenceScore = score,
                    MatchedSearchTerm = searchCandidate.SearchTerm,
                    Method = MatchMethod.FuzzySearch,
                    LocalData = searchCandidate.Source
                };

                matches.Add(match);
            }

            return matches
                .OrderByDescending(match => match.ConfidenceScore)
                .ToList();
        }

        private record SteamAppSummary(string? Name, int? ReleaseYear);

        private async Task<SteamAppSummary> GetSteamAppSummaryAsync(int steamId)
        {
            try
            {
                string url = $"{SteamDetailsUrl}{steamId}";
                var response = await _httpClient.GetStringAsync(url);
                using var jsonDoc = JsonDocument.Parse(response);

                if (jsonDoc.RootElement.TryGetProperty(steamId.ToString(), out var appData) &&
                    appData.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    appData.TryGetProperty("data", out var data))
                {
                    string? name = data.TryGetProperty("name", out var nameProp) ? nameProp.GetString()?.Trim() : null;

                    int? releaseYear = null;
                    if (data.TryGetProperty("release_date", out var relDate) &&
                        relDate.TryGetProperty("date", out var dateProp))
                    {
                        var m = Regex.Match(dateProp.GetString() ?? string.Empty, @"\b(19[7-9]\d|20\d{2})\b");
                        if (m.Success) releaseYear = int.Parse(m.Value);
                    }

                    return new SteamAppSummary(name, releaseYear);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Steam details: {ex.Message}");
            }
            return new SteamAppSummary(null, null);
        }

        private async Task<string?> GetSteamGameNameAsync(int steamId) =>
            (await GetSteamAppSummaryAsync(steamId)).Name;

        private async Task<int?> FindSteamIdAsync(string exePath)
        {
            GameMatch? match = await ResolveSteamMatchAsync(exePath);
            return match != null ? (int)match.SteamAppId : null;
        }

        private List<LocalGameCandidate> BuildCandidates(string exePath, string? nameHint = null)
        {
            var candidates = new List<LocalGameCandidate>();

            string executableName = Path.GetFileNameWithoutExtension(exePath);
            string directoryName = Path.GetDirectoryName(exePath) ?? string.Empty;
            string folderName = Directory.Exists(directoryName) ? new DirectoryInfo(directoryName).Name : executableName;

            string? productName = GetVersionInfoValue(exePath, "ProductName");
            string? fileDescription = GetVersionInfoValue(exePath, "FileDescription");
            string? productVersion = GetVersionInfoValue(exePath, "ProductVersion");
            string? legalCopyright = GetVersionInfoValue(exePath, "LegalCopyright");

            string? muiPath = string.IsNullOrEmpty(productName) && string.IsNullOrEmpty(fileDescription)
                ? FindMuiFile(exePath)
                : null;

            if (!string.IsNullOrEmpty(muiPath))
            {
                productName ??= GetVersionInfoValue(muiPath, "ProductName");
                fileDescription ??= GetVersionInfoValue(muiPath, "FileDescription");
                legalCopyright ??= GetVersionInfoValue(muiPath, "LegalCopyright");
            }

            var copyrightYears = ExtractCopyrightYears(legalCopyright);
            int? copyrightYear = copyrightYears.Count > 0 ? copyrightYears.Max() : null;

            var expectedSeriesNumbers = GetExpectedSeriesNumbers(nameHint, folderName, executableName);

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
                    MetadataSource = source,
                    CopyrightYear = copyrightYear,
                    CopyrightYears = copyrightYears,
                    ExpectedSeriesNumbers = expectedSeriesNumbers
                });
            }

            // Launcher-provided name (e.g. EA App install folder) gets highest priority
            if (!string.IsNullOrWhiteSpace(nameHint))
                AddCandidate(nameHint.Trim(), "launcher");

            // Filter PE metadata names that identify the engine, not the game
            if (!MetadataNameBlacklist.Contains(productName?.Trim() ?? string.Empty))
                AddCandidate(productName, "file_metadata");
            if (!string.IsNullOrWhiteSpace(fileDescription)
                && !string.Equals(productName, fileDescription, StringComparison.OrdinalIgnoreCase)
                && !MetadataNameBlacklist.Contains(fileDescription.Trim()))
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

        private List<LocalGameCandidate> BuildNameOnlyCandidates(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return new List<LocalGameCandidate>();
            }

            string cleanedName = GameNameCleaner.RemoveTrailingDomainTag(gameName);
            if (string.IsNullOrWhiteSpace(cleanedName))
            {
                return new List<LocalGameCandidate>();
            }

            return new List<LocalGameCandidate>
            {
                new()
                {
                    DetectedName = cleanedName,
                    FullPath = string.Empty,
                    ExecutableName = cleanedName,
                    MetadataSource = "launcher",
                    ExpectedSeriesNumbers = ExtractSeriesNumbers(NormalizeName(cleanedName))
                }
            };
        }

        private IEnumerable<SearchCandidate> GenerateSearchCandidates(LocalGameCandidate candidate)
        {
            var (priority, weight) = candidate.MetadataSource.ToLowerInvariant() switch
            {
                "steam_appid" => (0, 1.0f),
                "launcher"    => (0, 1.0f),
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
                        NormalizedLocalName = normalized, // score against original, not the shortened variant
                        Priority = priority + 1,
                        InitialWeight = MathF.Max(0.4f, weight - 0.1f),
                        Source = candidate
                    };
                }
            }
        }

        private IEnumerable<string> GenerateProgressiveVariants(string normalized)
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

        private IEnumerable<string> GenerateNumericVariants(string normalized)
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

        private string RemoveEditionSuffixes(string name)
        {
            var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (tokens.Count > 1 && EditionSuffixes.Contains(tokens[^1], StringComparer.OrdinalIgnoreCase))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            return string.Join(' ', tokens);
        }

        private async Task<List<SteamSearchResult>> SearchSteamAsync(string normalizedName, CancellationToken cancellationToken)
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

        private async Task DelayForRateLimitAsync(CancellationToken cancellationToken)
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

        private float CalculateMatchScore(string localName, string steamResult, LocalGameCandidate source)
        {
            if (string.IsNullOrWhiteSpace(localName) || string.IsNullOrWhiteSpace(steamResult))
                return 0f;

            var localTokens = localName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var steamTokens = steamResult.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var localSeriesNumbers = ExtractSeriesNumbers(localName);
            var steamSeriesNumbers = ExtractSeriesNumbers(steamResult);
            var effectiveLocalSeriesNumbers = localSeriesNumbers.Count > 0
                ? localSeriesNumbers
                : source.ExpectedSeriesNumbers;

            // Hard guard: series numbers must agree. This also understands roman numerals and
            // compact exe abbreviations such as re2.exe, so a local RE2 signal cannot match RE4.
            if (!SeriesNumbersMatch(effectiveLocalSeriesNumbers, steamSeriesNumbers))
                return 0f;

            // If a launcher/folder hint carried a series number, apply that context to every
            // metadata/executable candidate as well. This prevents generic PE metadata like
            // "Resident Evil" from bypassing the local "Resident Evil 2" folder signal.
            if (source.ExpectedSeriesNumbers.Count > 0 &&
                (steamSeriesNumbers.Count == 0 || !SetEquals(source.ExpectedSeriesNumbers, steamSeriesNumbers)))
            {
                return 0f;
            }

            if (localName.Equals(steamResult, StringComparison.OrdinalIgnoreCase))
                return 1.0f;

            float score = 0f;

            float levenshteinSimilarity = 1f - (float)Helpers.StringSimilarity.LevenshteinDistance(localName, steamResult) /
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

            // Penalise token mismatches in both directions — catches series variants where token
            // counts are equal (e.g. "need for speed rivals" vs "need for speed heat": both 4 tokens,
            // but "rivals"/"heat" are mutually exclusive significant tokens → -0.40 total).
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "for", "the", "of", "a", "an", "and", "in", "on", "at", "to" };

            int significantMissing = localTokens
                .Where(lt => !stopWords.Contains(lt) && !steamTokens.Any(st => st.Equals(lt, StringComparison.OrdinalIgnoreCase)))
                .Count();
            score -= significantMissing * 0.20f;

            int significantExtra = steamTokens
                .Where(st => !stopWords.Contains(st) && !localTokens.Any(lt => lt.Equals(st, StringComparison.OrdinalIgnoreCase)))
                .Count();
            score -= significantExtra * 0.20f;

            return Math.Clamp(score, 0f, 1f);
        }

        private IReadOnlySet<int> GetExpectedSeriesNumbers(string? nameHint, string folderName, string executableName)
        {
            var fromHint = ExtractSeriesNumbers(NormalizeName(nameHint));
            if (fromHint.Count > 0)
            {
                return fromHint;
            }

            if (!DeprioritizedTerms.Any(term => folderName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                var fromFolder = ExtractSeriesNumbers(NormalizeName(folderName));
                if (fromFolder.Count > 0)
                {
                    return fromFolder;
                }
            }

            return ExtractSeriesNumbers(NormalizeName(executableName));
        }

        private bool SeriesNumbersMatch(IReadOnlySet<int> localNumbers, IReadOnlySet<int> steamNumbers)
        {
            if (localNumbers.Count == 0 && steamNumbers.Count == 0)
            {
                return true;
            }

            return localNumbers.Count == steamNumbers.Count && SetEquals(localNumbers, steamNumbers);
        }

        private static bool SetEquals(IReadOnlySet<int> left, IReadOnlySet<int> right) =>
            left.Count == right.Count && left.All(right.Contains);

        private IReadOnlySet<int> ExtractSeriesNumbers(string? normalizedName)
        {
            var numbers = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return numbers;
            }

            var nonSeriesCompactTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "x64", "x86", "win64", "win32", "dx11", "dx12"
            };

            foreach (string token in normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, out int numeric))
                {
                    AddSeriesNumber(numbers, numeric);
                    continue;
                }

                if (TryParseRoman(token, out int roman))
                {
                    AddSeriesNumber(numbers, roman);
                    continue;
                }

                if (!nonSeriesCompactTokens.Contains(token))
                {
                    var compactMatch = Regex.Match(token, @"^[a-z]{1,5}(\d{1,2})$", RegexOptions.IgnoreCase);
                    if (compactMatch.Success && int.TryParse(compactMatch.Groups[1].Value, out int compact))
                    {
                        AddSeriesNumber(numbers, compact);
                    }
                }
            }

            return numbers;
        }

        private static void AddSeriesNumber(HashSet<int> numbers, int value)
        {
            if (value > 0 && value <= 100)
            {
                numbers.Add(value);
            }
        }

        private float CalculateTokenOverlap(string s1, string s2)
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


        private string NormalizeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string value = GameNameCleaner.RemoveTrailingDomainTag(raw);
            value = value.Replace('_', ' ').Replace('+', ' ');
            value = CamelCaseRegex.Replace(value, " $1");
            value = RemoveFileExtension(value);
            value = TrademarkRegex.Replace(value, "");
            value = BareTrademarkTokenRegex.Replace(value, " ");
            value = SpecialCharRegex.Replace(value, " ");
            value = value.Replace("/", " ");
            value = SanitizeUmlauts(value);
            value = TrimAffixes(value);
            value = value.ToLowerInvariant();
            value = RemoveEditionSuffixes(value);
            value = MultiSpaceRegex.Replace(value, " ").Trim();
            return value;
        }

        private string RemoveFileExtension(string value)
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

        private string TrimAffixes(string value)
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

        private string SanitizeUmlauts(string value)
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

        private string SanitizeRawName(string value)
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

        private string? FindMuiFile(string exePath)
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

        public string? GetBestName(string exePath)
        {
            return GetPrioritizedNames(exePath).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(exePath);
        }

        private List<string> GetPrioritizedNames(string exePath)
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

        private int? GetSteamAppIdFromFile(string exePath)
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

        private async Task<int?> FindRawgIdAsync(string exePath)
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

        public async Task<int?> FindRawgIdByNameAsync(string gameName, RawgValidationMode mode = RawgValidationMode.Strict)
        {
            string cleanedName = GameNameCleaner.RemoveTrailingDomainTag(gameName);
            if (string.IsNullOrWhiteSpace(cleanedName)) return null;
            return await _gameDetails.ValidateGameAsync(cleanedName, mode);
        }

        private bool TryParseRoman(string token, out int value)
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

        private string? ToRoman(int number)
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
