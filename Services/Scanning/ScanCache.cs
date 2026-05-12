using Codec.Helpers;
using Codec.Services.Scanning.Scanners;
using Codec.Services.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services.Scanning
{
    /// <summary>
    /// Persists small cache entries for folders that were already validated so that
    /// subsequent scans can reuse the metadata instead of executing the full funnel again.
    /// </summary>
    public sealed class ScanCache
    {
        private const string CacheFileName = "scan-cache.json";
        private const int CurrentCacheVersion = 3;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, CachedScanResult> _entries;

        private ScanCache(Dictionary<string, CachedScanResult> entries)
        {
            _entries = entries;
        }

        public static async Task<ScanCache> LoadAsync()
        {
            try
            {
                string path = GetCachePath();
                if (!File.Exists(path))
                {
                    return new ScanCache(new Dictionary<string, CachedScanResult>(StringComparer.OrdinalIgnoreCase));
                }

                await using var fs = File.OpenRead(path);
                var payload = await JsonSerializer.DeserializeAsync<List<CachedScanResult>>(fs, JsonOptions)
                               ?? new List<CachedScanResult>();

                var deduped = payload
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.FolderPath))
                    .GroupBy(entry => entry.FolderPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(e => e.CachedAtUtc).First())
                    .Select(NormalizeCachedSource)
                    .ToDictionary(entry => entry.FolderPath, StringComparer.OrdinalIgnoreCase);

                Debug.WriteLine($"Loaded scan cache with {deduped.Count} entries");
                return new ScanCache(deduped);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Scan cache load failed: {ex.Message}");
                return new ScanCache(new Dictionary<string, CachedScanResult>(StringComparer.OrdinalIgnoreCase));
            }
        }

        public bool TryGetValid(GameCandidate candidate, out CachedScanResult result)
        {
            result = null!;
            if (!_entries.TryGetValue(candidate.FolderPath, out var entry))
            {
                return false;
            }

            if (entry.CacheVersion != CurrentCacheVersion)
            {
                _entries.Remove(candidate.FolderPath);
                return false;
            }

            if (!string.Equals(entry.EpicAppId, candidate.EpicAppId, StringComparison.OrdinalIgnoreCase))
            {
                _entries.Remove(candidate.FolderPath);
                return false;
            }

            bool hasExecutable = !string.IsNullOrWhiteSpace(entry.ExecutablePath);
            bool hasLaunchScript = !string.IsNullOrWhiteSpace(entry.LaunchScriptPath)
                && File.Exists(entry.LaunchScriptPath);
            bool candidateHasLaunchScript = !string.IsNullOrWhiteSpace(candidate.LaunchScriptPath)
                && File.Exists(candidate.LaunchScriptPath);

            if (candidateHasLaunchScript &&
                !string.Equals(entry.LaunchScriptPath, candidate.LaunchScriptPath, StringComparison.OrdinalIgnoreCase))
            {
                _entries.Remove(candidate.FolderPath);
                return false;
            }

            if (!Directory.Exists(entry.FolderPath) ||
                (hasExecutable && !File.Exists(entry.ExecutablePath)) ||
                (!hasExecutable && !hasLaunchScript && !CanTrustMissingExecutable(entry)))
            {
                _entries.Remove(candidate.FolderPath);
                return false;
            }

            if (entry.DirectoryTimestampUtcTicks > 0)
            {
                long? currentDirTimestamp = TimestampUtility.GetDirectoryTimestamp(entry.FolderPath);
                if (!currentDirTimestamp.HasValue || currentDirTimestamp.Value > entry.DirectoryTimestampUtcTicks)
                {
                    _entries.Remove(candidate.FolderPath);
                    return false;
                }
            }

            if (entry.ExecutableTimestampUtcTicks > 0)
            {
                long? currentExeTimestamp = TimestampUtility.GetFileTimestamp(entry.ExecutablePath);
                if (!currentExeTimestamp.HasValue || currentExeTimestamp.Value > entry.ExecutableTimestampUtcTicks)
                {
                    _entries.Remove(candidate.FolderPath);
                    return false;
                }
            }

            result = entry;
            return true;
        }

        public void Invalidate(string folderPath)
        {
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                _entries.Remove(folderPath);
            }
        }

        public void Upsert(GameCandidate candidate, string resolvedName, string executablePath, int? steamId, int? rawgId, string? launchScriptPath, int? igdbId = null)
        {
            long? dirTimestamp = TimestampUtility.GetDirectoryTimestamp(candidate.FolderPath);
            long? exeTimestamp = TimestampUtility.GetFileTimestamp(executablePath);

            dirTimestamp ??= 0;
            exeTimestamp ??= 0;

            _entries[candidate.FolderPath] = new CachedScanResult
            {
                CacheVersion = CurrentCacheVersion,
                FolderPath = candidate.FolderPath,
                ExecutablePath = executablePath,
                GameName = resolvedName,
                ImportSource = PlatformSourceNames.NormalizeImportSource(candidate.Source),
                SteamAppId = steamId,
                EpicAppId = candidate.EpicAppId,
                RawgId = rawgId,
                IgdbId = igdbId,
                LaunchScriptPath = launchScriptPath,
                DirectoryTimestampUtcTicks = dirTimestamp.Value,
                ExecutableTimestampUtcTicks = exeTimestamp.Value,
                CachedAtUtc = DateTime.UtcNow
            };
        }

        public async Task SaveAsync()
        {
            try
            {
                string path = GetCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var fs = File.Create(path);
                await JsonSerializer.SerializeAsync(fs, _entries.Values, JsonOptions);
                Debug.WriteLine($"Persisted scan cache with {_entries.Count} entries");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Scan cache save failed: {ex.Message}");
            }
        }

        private static string GetCachePath()
        {
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LibraryStorageService.AppDataFolderName);
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, CacheFileName);
        }

        public sealed class CachedScanResult
        {
            public int CacheVersion { get; init; }
            public required string FolderPath { get; init; }
            public required string ExecutablePath { get; init; }
            public required string GameName { get; init; }
            public required string ImportSource { get; init; }
            public int? SteamAppId { get; init; }
            public string? EpicAppId { get; init; }
            public int? RawgId { get; init; }
            public int? IgdbId { get; init; }
            public string? LaunchScriptPath { get; init; }
            public long DirectoryTimestampUtcTicks { get; init; }
            public long ExecutableTimestampUtcTicks { get; init; }
            public DateTime CachedAtUtc { get; init; }
        }

        private static CachedScanResult NormalizeCachedSource(CachedScanResult entry)
        {
            string normalizedSource = PlatformSourceNames.NormalizeImportSource(entry.ImportSource);
            if (string.Equals(normalizedSource, entry.ImportSource, StringComparison.Ordinal))
            {
                return entry;
            }

            return new CachedScanResult
            {
                CacheVersion = entry.CacheVersion,
                FolderPath = entry.FolderPath,
                ExecutablePath = entry.ExecutablePath,
                GameName = entry.GameName,
                ImportSource = normalizedSource,
                SteamAppId = entry.SteamAppId,
                EpicAppId = entry.EpicAppId,
                RawgId = entry.RawgId,
                IgdbId = entry.IgdbId,
                LaunchScriptPath = entry.LaunchScriptPath,
                DirectoryTimestampUtcTicks = entry.DirectoryTimestampUtcTicks,
                ExecutableTimestampUtcTicks = entry.ExecutableTimestampUtcTicks,
                CachedAtUtc = entry.CachedAtUtc
            };
        }

        private static bool CanTrustMissingExecutable(CachedScanResult entry) =>
            entry.SteamAppId.HasValue ||
            !string.IsNullOrWhiteSpace(entry.EpicAppId) ||
            string.Equals(entry.ImportSource, PlatformSourceNames.RiotGames, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.ImportSource, PlatformSourceNames.Steam, StringComparison.OrdinalIgnoreCase);

        private static class TimestampUtility
        {
            public static long? GetDirectoryTimestamp(string path)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        return null;
                    }
                    return Directory.GetLastWriteTimeUtc(path).Ticks;
                }
                catch
                {
                    return null;
                }
            }

            public static long? GetFileTimestamp(string path)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return null;
                    }
                    return File.GetLastWriteTimeUtc(path).Ticks;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
