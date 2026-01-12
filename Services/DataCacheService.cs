using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
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
        private sealed record WarmupRequest(string Url, TimeSpan? Ttl);

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Http = new();
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);
        private static readonly Channel<WarmupRequest> WarmupChannel = Channel.CreateUnbounded<WarmupRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private static readonly CancellationTokenSource WarmupCts = new();
        private static readonly Task WarmupLoop = ProcessWarmupsAsync();

        static DataCacheService()
        {
            _ = WarmupLoop;
        }

        public static async Task<string> GetStringAsync(string url, TimeSpan? ttl = null)
        {
            TimeSpan effectiveTtl = ttl ?? DefaultTtl;
            DateTime nowUtc = DateTime.UtcNow;

            if (TryGetFresh(url, effectiveTtl, nowUtc, out var cached))
            {
                return cached;
            }

            string payload = await Http.GetStringAsync(url).ConfigureAwait(false);
            Cache[url] = new CacheEntry(payload, nowUtc);
            return payload;
        }

        public static void QueueWarmup(string url, TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            WarmupChannel.Writer.TryWrite(new WarmupRequest(url, ttl));
        }

        public static void Clear()
        {
            Cache.Clear();
        }

        private static bool TryGetFresh(string url, TimeSpan ttl, DateTime nowUtc, out string content)
        {
            if (Cache.TryGetValue(url, out var entry) && nowUtc - entry.TimestampUtc < ttl)
            {
                content = entry.Content;
                return true;
            }

            content = string.Empty;
            return false;
        }

        private static async Task ProcessWarmupsAsync()
        {
            try
            {
                await foreach (var request in WarmupChannel.Reader.ReadAllAsync(WarmupCts.Token))
                {
                    var effectiveTtl = request.Ttl ?? DefaultTtl;
                    if (TryGetFresh(request.Url, effectiveTtl, DateTime.UtcNow, out _))
                    {
                        continue;
                    }

                    try
                    {
                        await GetStringAsync(request.Url, request.Ttl).ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort; ignore failures
                    }

                    try
                    {
                        await Task.Delay(150, WarmupCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }
    }
}
