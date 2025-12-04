using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services
{
    /// <summary>
    /// Persists small cache entries for folders that were already validated so that
    /// subsequent scans can reuse the metadata instead of executing the full funnel again.
    /// </summary>
    public sealed class ScanCache
    {
        private const string CacheFileName = "scan-cache.json";
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

            if (!Directory.Exists(entry.FolderPath) || !File.Exists(entry.ExecutablePath))
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

        public void Upsert(GameCandidate candidate, string resolvedName, string executablePath, int? steamId, int? rawgId)
        {
            long? dirTimestamp = TimestampUtility.GetDirectoryTimestamp(candidate.FolderPath);
            long? exeTimestamp = TimestampUtility.GetFileTimestamp(executablePath);

            if (!dirTimestamp.HasValue || !exeTimestamp.HasValue)
            {
                // If timestamps cannot be obtained we still keep the entry but mark timestamps as 0
                dirTimestamp ??= 0;
                exeTimestamp ??= 0;
            }

            _entries[candidate.FolderPath] = new CachedScanResult
            {
                FolderPath = candidate.FolderPath,
                ExecutablePath = executablePath,
                GameName = resolvedName,
                ImportSource = candidate.Source,
                SteamAppId = steamId,
                RawgId = rawgId,
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
            string baseDir = LibraryStorageService.EnsureStorageInitialized();
            return Path.Combine(baseDir, CacheFileName);
        }

        public sealed class CachedScanResult
        {
            public required string FolderPath { get; init; }
            public required string ExecutablePath { get; init; }
            public required string GameName { get; init; }
            public required string ImportSource { get; init; }
            public int? SteamAppId { get; init; }
            public int? RawgId { get; init; }
            public long DirectoryTimestampUtcTicks { get; init; }
            public long ExecutableTimestampUtcTicks { get; init; }
            public DateTime CachedAtUtc { get; init; }
        }

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
