using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Codec.Services.Storage
{
    /// <summary>
    /// Two-tier (memory + disk) cache for HTTP GET responses.
    /// Each caller specifies a partition name so data is organized per-service.
    /// Disk path: %LocalAppData%/Codec Game Library/Cache/{partition}/{urlHash}.json
    /// </summary>
    public sealed class MetadataCache
    {
        /// <summary>
        /// Shared singleton for use by static services. Will be removed when services
        /// convert to instance classes with constructor injection (Phase 4).
        /// </summary>
        public static MetadataCache Shared { get; } = new();

        private sealed record CacheEntry(string Content, DateTime TimestampUtc);
        private sealed record WarmupRequest(string Partition, string Url, TimeSpan? MaxAge);
        private sealed record DiskEntry(string Url, string Content, DateTime CachedAtUtc);

        private static readonly JsonSerializerOptions DiskJsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private readonly ConcurrentDictionary<string, CacheEntry> _memory = new(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _http = new();
        private readonly string _baseCacheDir;
        private readonly Channel<WarmupRequest> _warmupChannel;
        private readonly CancellationTokenSource _warmupCts = new();

        public MetadataCache()
        {
            _baseCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Codec Game Library", "Cache");

            _warmupChannel = Channel.CreateUnbounded<WarmupRequest>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _ = ProcessWarmupsAsync();
        }

        /// <summary>
        /// Returns cached content if fresh (memory or disk), otherwise fetches from network.
        /// </summary>
        public async Task<string> GetOrFetchAsync(string partition, string url, TimeSpan? maxAge = null)
        {
            var effectiveMaxAge = maxAge ?? TimeSpan.FromMinutes(30);
            string cacheKey = BuildCacheKey(partition, url);
            var now = DateTime.UtcNow;

            // Tier 1: memory
            if (_memory.TryGetValue(cacheKey, out var memEntry) && now - memEntry.TimestampUtc < effectiveMaxAge)
            {
                return memEntry.Content;
            }

            // Tier 2: disk
            string? diskContent = await ReadDiskAsync(partition, url, effectiveMaxAge);
            if (diskContent != null)
            {
                _memory[cacheKey] = new CacheEntry(diskContent, now);
                return diskContent;
            }

            // Tier 3: network
            string payload = await _http.GetStringAsync(url).ConfigureAwait(false);
            _memory[cacheKey] = new CacheEntry(payload, now);
            _ = WriteDiskAsync(partition, url, payload);
            return payload;
        }

        /// <summary>
        /// Returns cached content (memory or disk) without fetching. Returns null if nothing cached or expired.
        /// </summary>
        public async Task<string?> GetCachedAsync(string partition, string url, TimeSpan? maxAge = null)
        {
            var effectiveMaxAge = maxAge ?? TimeSpan.FromMinutes(30);
            string cacheKey = BuildCacheKey(partition, url);
            var now = DateTime.UtcNow;

            if (_memory.TryGetValue(cacheKey, out var memEntry) && now - memEntry.TimestampUtc < effectiveMaxAge)
            {
                return memEntry.Content;
            }

            return await ReadDiskAsync(partition, url, effectiveMaxAge);
        }

        /// <summary>
        /// Enqueues a URL for background cache population.
        /// </summary>
        public void QueueWarmup(string partition, string url, TimeSpan? maxAge = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _warmupChannel.Writer.TryWrite(new WarmupRequest(partition, url, maxAge));
        }

        public void ClearPartition(string partition)
        {
            // Clear memory entries for this partition
            foreach (var key in _memory.Keys)
            {
                if (key.StartsWith(partition + "|", StringComparison.OrdinalIgnoreCase))
                    _memory.TryRemove(key, out _);
            }

            // Clear disk
            string partitionDir = Path.Combine(_baseCacheDir, partition);
            if (Directory.Exists(partitionDir))
            {
                try { Directory.Delete(partitionDir, recursive: true); } catch { }
            }
        }

        public void ClearAll()
        {
            _memory.Clear();
            if (Directory.Exists(_baseCacheDir))
            {
                try { Directory.Delete(_baseCacheDir, recursive: true); } catch { }
            }
        }

        private static string BuildCacheKey(string partition, string url) => $"{partition}|{url}";

        private string GetDiskPath(string partition, string url)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
            string fileName = Convert.ToHexString(hash)[..32] + ".json";
            return Path.Combine(_baseCacheDir, partition, fileName);
        }

        private async Task<string?> ReadDiskAsync(string partition, string url, TimeSpan maxAge)
        {
            string path = GetDiskPath(partition, url);
            if (!File.Exists(path)) return null;

            try
            {
                await using var fs = File.OpenRead(path);
                var entry = await JsonSerializer.DeserializeAsync<DiskEntry>(fs, DiskJsonOptions);
                if (entry == null) return null;

                if (DateTime.UtcNow - entry.CachedAtUtc > maxAge) return null;

                return entry.Content;
            }
            catch
            {
                return null;
            }
        }

        private async Task WriteDiskAsync(string partition, string url, string content)
        {
            try
            {
                string path = GetDiskPath(partition, url);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var entry = new DiskEntry(url, content, DateTime.UtcNow);
                await using var fs = File.Create(path);
                await JsonSerializer.SerializeAsync(fs, entry, DiskJsonOptions);
            }
            catch
            {
                // Best effort - don't fail the caller
            }
        }

        private async Task ProcessWarmupsAsync()
        {
            try
            {
                await foreach (var request in _warmupChannel.Reader.ReadAllAsync(_warmupCts.Token))
                {
                    var effectiveMaxAge = request.MaxAge ?? TimeSpan.FromMinutes(30);
                    string cacheKey = BuildCacheKey(request.Partition, request.Url);

                    // Skip if already fresh in memory
                    if (_memory.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow - entry.TimestampUtc < effectiveMaxAge)
                    {
                        continue;
                    }

                    try
                    {
                        await GetOrFetchAsync(request.Partition, request.Url, request.MaxAge).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best effort
                    }

                    try
                    {
                        await Task.Delay(150, _warmupCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown
            }
        }
    }
}
