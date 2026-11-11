using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Codec.Services.Scanning;

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

        /// <summary>
        /// Memory budget in UTF-16 characters (about 32 MB). The disk tier keeps every response, so
        /// memory only has to hold the working set: one open detail page needs about six bodies,
        /// and the import workers a few lookups each. Budgeting by size rather than count matters
        /// because bodies range from about 1 KB (review summaries) to over 100 KB (app details).
        /// </summary>
        internal const long DefaultMemoryBudgetChars = 16L * 1024 * 1024;

        private readonly MemoryTier _memory;
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _http;
        private readonly ScanResourceLimiter? _resourceLimiter;
        private readonly string _baseCacheDir;
        private readonly Channel<WarmupRequest> _warmupChannel;
        private readonly CancellationTokenSource _warmupCts = new();

        /// <param name="cacheRoot">Disk tier root. Defaults to the cache folder in local app data.</param>
        /// <param name="httpHandler">Transport for fetches, so tests never reach the network.</param>
        public MetadataCache(ScanResourceLimiter? resourceLimiter = null, string? cacheRoot = null, HttpMessageHandler? httpHandler = null)
            : this(resourceLimiter, cacheRoot, httpHandler, DefaultMemoryBudgetChars)
        {
        }

        internal MetadataCache(ScanResourceLimiter? resourceLimiter, string? cacheRoot, HttpMessageHandler? httpHandler, long memoryBudgetChars)
        {
            _resourceLimiter = resourceLimiter;
            _memory = new MemoryTier(memoryBudgetChars);
            // The default 100 s would let one hung proxy connection hold a network slot for that long.
            _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
            _http.Timeout = TimeSpan.FromSeconds(30);
            _baseCacheDir = cacheRoot ?? Path.Combine(
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
        public async Task<string> GetOrFetchAsync(string partition, string url, TimeSpan? maxAge = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                _memory.Set(cacheKey, new CacheEntry(diskContent, now));
                return diskContent;
            }

            // Tier 3: coalesced network request. Parallel imports asking for the
            // same URL share one fetch and one atomic disk write.
            var fetch = _inFlight.GetOrAdd(cacheKey, _ => new Lazy<Task<string>>(
                () => FetchAndStoreAsync(partition, url, cacheKey),
                LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                // The shared fetch is not tied to this caller's token: a cancel from one
                // importer must not fail the others waiting on the same URL.
                return await fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A failed refresh must not cost the caller an expired answer it already had.
                string? stale = _memory.TryGetValue(cacheKey, out var staleEntry)
                    ? staleEntry.Content
                    : await ReadDiskAsync(partition, url, TimeSpan.MaxValue).ConfigureAwait(false);
                if (stale != null)
                {
                    return stale;
                }

                throw;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(cacheKey, fetch));
            }
        }

        private async Task<string> FetchAndStoreAsync(string partition, string url, string cacheKey)
        {
            string payload = _resourceLimiter is null
                ? await _http.GetStringAsync(url).ConfigureAwait(false)
                : await _resourceLimiter.RunNetworkAsync(
                    ct => _http.GetStringAsync(url, ct),
                    _warmupCts.Token).ConfigureAwait(false);

            _memory.Set(cacheKey, new CacheEntry(payload, DateTime.UtcNow));
            await WriteDiskAsync(partition, url, payload).ConfigureAwait(false);
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
            _memory.RemoveWithPrefix(partition + "|");

            // Clear disk
            string partitionDir = Path.Combine(_baseCacheDir, partition);
            if (Directory.Exists(partitionDir))
            {
                try { Directory.Delete(partitionDir, recursive: true); } catch { }
            }
        }

        internal int MemoryEntryCount => _memory.Count;
        internal long MemoryChars => _memory.Chars;

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
                string tempPath = path + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var fs = File.Create(tempPath))
                    {
                        await JsonSerializer.SerializeAsync(fs, entry, DiskJsonOptions);
                    }

                    File.Move(tempPath, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
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
                        if (_resourceLimiter == null)
                            await GetOrFetchAsync(request.Partition, request.Url, request.MaxAge).ConfigureAwait(false);
                        else
                            await _resourceLimiter.RunAsBackgroundAsync(
                                () => GetOrFetchAsync(request.Partition, request.Url, request.MaxAge)).ConfigureAwait(false);
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

        /// <summary>Least recently used memory tier, bounded by total content size.</summary>
        private sealed class MemoryTier
        {
            private readonly object _lock = new();
            private readonly Dictionary<string, LinkedListNode<(string Key, CacheEntry Entry)>> _nodes = new(StringComparer.OrdinalIgnoreCase);
            private readonly LinkedList<(string Key, CacheEntry Entry)> _order = new();
            private readonly long _budgetChars;
            private long _chars;

            public MemoryTier(long budgetChars) => _budgetChars = budgetChars;

            public int Count
            {
                get { lock (_lock) return _nodes.Count; }
            }

            public long Chars
            {
                get { lock (_lock) return _chars; }
            }

            public bool TryGetValue(string key, out CacheEntry entry)
            {
                lock (_lock)
                {
                    if (_nodes.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                        entry = node.Value.Entry;
                        return true;
                    }
                }

                entry = null!;
                return false;
            }

            public void Set(string key, CacheEntry entry)
            {
                lock (_lock)
                {
                    RemoveNode(key);
                    // A body larger than the whole budget would only flush everything else out.
                    if (entry.Content.Length > _budgetChars)
                        return;

                    _nodes[key] = _order.AddFirst((key, entry));
                    _chars += entry.Content.Length;
                    while (_chars > _budgetChars && _order.Last is { } oldest)
                        RemoveNode(oldest.Value.Key);
                }
            }

            public void RemoveWithPrefix(string prefix)
            {
                lock (_lock)
                {
                    foreach (var key in _nodes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        RemoveNode(key);
                }
            }

            private void RemoveNode(string key)
            {
                if (_nodes.Remove(key, out var node))
                {
                    _order.Remove(node);
                    _chars -= node.Value.Entry.Content.Length;
                }
            }
        }
    }
}
