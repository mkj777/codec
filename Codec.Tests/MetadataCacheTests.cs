using Codec.Services.Storage;
using System.Net;
using System.Text;
using Xunit;

namespace Codec.Tests
{
    public sealed class MetadataCacheTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"codec-metadata-cache-{Guid.NewGuid():N}");
        private readonly CountingHandler _handler = new();

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public async Task MemoryTier_EvictsLeastRecentlyUsed_ToStayWithinTheBudget()
        {
            // Each body is 9 chars, so a 25 char budget holds two.
            var cache = CreateCache(memoryBudgetChars: 25);

            await cache.GetOrFetchAsync("p", Url("a"));
            await cache.GetOrFetchAsync("p", Url("b"));
            await cache.GetOrFetchAsync("p", Url("a"));
            await cache.GetOrFetchAsync("p", Url("c"));

            Assert.Equal(2, cache.MemoryEntryCount);
            Assert.True(cache.MemoryChars <= 25);
            Assert.Equal(3, _handler.Calls);
        }

        [Fact]
        public async Task EvictedEntry_IsServedFromDisk_WithoutAFetch()
        {
            var cache = CreateCache(memoryBudgetChars: 15);

            string first = await cache.GetOrFetchAsync("p", Url("a"));
            await cache.GetOrFetchAsync("p", Url("b"));
            _handler.Fail = true;

            string again = await cache.GetOrFetchAsync("p", Url("a"));

            Assert.Equal(first, again);
            Assert.Equal(2, _handler.Calls);
            Assert.Equal(1, cache.MemoryEntryCount);
        }

        [Fact]
        public async Task ExpiredEntry_IsServed_WhenTheRefreshFails()
        {
            var cache = CreateCache(memoryBudgetChars: MetadataCache.DefaultMemoryBudgetChars);
            string first = await cache.GetOrFetchAsync("p", Url("a"));
            _handler.Fail = true;

            string stale = await cache.GetOrFetchAsync("p", Url("a"), maxAge: TimeSpan.Zero);

            Assert.Equal(first, stale);
            Assert.Equal(2, _handler.Calls);
        }

        [Fact]
        public async Task ExpiredEntry_IsServedFromDisk_WhenTheRefreshFailsAfterEviction()
        {
            var cache = CreateCache(memoryBudgetChars: 15);
            string first = await cache.GetOrFetchAsync("p", Url("a"));
            await cache.GetOrFetchAsync("p", Url("b"));
            _handler.Fail = true;

            string stale = await cache.GetOrFetchAsync("p", Url("a"), maxAge: TimeSpan.Zero);

            Assert.Equal(first, stale);
        }

        [Fact]
        public async Task FailedFetch_Throws_WhenNothingWasEverCached()
        {
            var cache = CreateCache(memoryBudgetChars: MetadataCache.DefaultMemoryBudgetChars);
            _handler.Fail = true;

            await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetOrFetchAsync("p", Url("a")));
        }

        private MetadataCache CreateCache(long memoryBudgetChars) =>
            new(resourceLimiter: null, cacheRoot: _root, httpHandler: _handler, memoryBudgetChars: memoryBudgetChars);

        private static string Url(string id) => $"https://example.invalid/{id}";

        private sealed class CountingHandler : HttpMessageHandler
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);
            public bool Fail { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                int call = Interlocked.Increment(ref _calls);
                if (Fail)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

                // Nine chars per body, distinct per call so a refetch is visible.
                string body = $"\"{request.RequestUri!.Segments[^1]}-{call:D5}\"";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
