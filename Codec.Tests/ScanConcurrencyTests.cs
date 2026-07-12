using Codec.Services.Resolving;
using Codec.Services.Scanning;
using Xunit;

namespace Codec.Tests
{
    public sealed class ScanConcurrencyTests
    {
        [Theory]
        [InlineData(1, 2, 2, 2, 1, 1, 8)]
        [InlineData(8, 4, 2, 2, 2, 1, 8)]
        [InlineData(16, 6, 4, 4, 4, 2, 16)]
        [InlineData(64, 6, 4, 4, 4, 2, 16)]
        public void AdaptiveOptions_AreClampedPerResource(
            int processors,
            int heuristic,
            int validation,
            int imports,
            int disk,
            int folderSize,
            int queueCapacity)
        {
            var options = ScanConcurrencyOptions.CreateAdaptive(processors);

            Assert.Equal(heuristic, options.HeuristicWorkers);
            Assert.Equal(validation, options.ValidationWorkers);
            Assert.Equal(imports, options.ImportWorkers);
            Assert.Equal(disk, options.DiskOperations);
            Assert.Equal(folderSize, options.FolderSizeOperations);
            Assert.Equal(queueCapacity, options.ImportQueueCapacity);
        }

        [Fact]
        public async Task NetworkLimiter_NeverExceedsConfiguredConcurrency()
        {
            var options = new ScanConcurrencyOptions(2, 2, 2, 3, 1, 1, 8, 4);
            var limiter = new ScanResourceLimiter(options);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var saturated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0;
            int peak = 0;

            var tasks = Enumerable.Range(0, 10).Select(_ => limiter.RunNetworkAsync(async ct =>
            {
                int current = Interlocked.Increment(ref active);
                UpdatePeak(ref peak, current);
                if (current == 3) saturated.TrySetResult(true);
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref active);
            }, TestContext.Current.CancellationToken)).ToArray();

            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(3, Volatile.Read(ref peak));
            release.TrySetResult(true);
            await Task.WhenAll(tasks);
        }

        [Fact]
        public void SteamScanner_DefaultRateLimitMatchesAp10()
        {
            var config = new GameNameService.ScannerConfig();
            Assert.Equal(3, config.MaxConcurrentApiRequests);
            Assert.Equal(TimeSpan.FromMilliseconds(200), config.RateLimitDelay);
        }

        private static void UpdatePeak(ref int peak, int current)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref peak);
                if (current <= observed) return;
            }
            while (Interlocked.CompareExchange(ref peak, current, observed) != observed);
        }
    }
}
