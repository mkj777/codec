using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;

namespace Codec.Services
{
    /// <summary>
    /// Simple in-memory cache for HTTP GET string payloads with TTL.
    /// Keeps data fresh by refetching when entries expire.
    /// </summary>
    public static class DataCacheService
    {
        private sealed record CacheEntry(string Content, DateTime TimestampUtc);

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Http = new();
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

        public static async Task<string> GetStringAsync(string url, TimeSpan? ttl = null)
        {
            TimeSpan effectiveTtl = ttl ?? DefaultTtl;
            DateTime nowUtc = DateTime.UtcNow;

            if (Cache.TryGetValue(url, out var entry) && nowUtc - entry.TimestampUtc < effectiveTtl)
            {
                return entry.Content;
            }

            string payload = await Http.GetStringAsync(url).ConfigureAwait(false);
            Cache[url] = new CacheEntry(payload, nowUtc);
            return payload;
        }

        public static void Clear() => Cache.Clear();
    }
}
