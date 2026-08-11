using System;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Scanning
{
    public sealed record ScanConcurrencyOptions(
        int HeuristicWorkers,
        int ValidationWorkers,
        int ImportWorkers,
        int NetworkOperations,
        int DiskOperations,
        int FolderSizeOperations,
        int ImportQueueCapacity,
        int ValidationBufferCapacity)
    {
        public int BackgroundWorkers { get; init; } = 4;
        public int BackgroundNetworkOperations { get; init; } = 8;
        public int ForegroundReservedNetworkOperations { get; init; } = 2;
        public TimeSpan BackgroundRequestDelay { get; init; } = TimeSpan.FromMilliseconds(75);

        public static ScanConcurrencyOptions CreateAdaptive(int? processorCount = null)
        {
            int processors = Math.Max(1, processorCount ?? Environment.ProcessorCount);
            int validationWorkers = Math.Clamp(processors / 4, 2, 4);
            int importWorkers = Math.Clamp(processors / 4, 2, 4);

            return new ScanConcurrencyOptions(
                HeuristicWorkers: Math.Clamp(processors / 2, 2, 6),
                ValidationWorkers: validationWorkers,
                ImportWorkers: importWorkers,
                NetworkOperations: 10,
                DiskOperations: Math.Clamp(processors / 4, 1, 4),
                FolderSizeOperations: Math.Clamp(processors / 8, 1, 2),
                ImportQueueCapacity: Math.Max(8, importWorkers * 4),
                ValidationBufferCapacity: Math.Max(4, validationWorkers * 2));
        }
    }

    public sealed class ScanResourceLimiter
    {
        private readonly SemaphoreSlim _network;
        private readonly SemaphoreSlim _backgroundNetwork;
        private readonly SemaphoreSlim _disk;
        private readonly SemaphoreSlim _folderSize;
        private readonly AsyncLocal<int> _backgroundDepth = new();
        private readonly object _foregroundLock = new();
        private TaskCompletionSource<bool> _foregroundIdle = CompletedSignal();
        private int _foregroundDemand;

        public ScanResourceLimiter(ScanConcurrencyOptions options)
        {
            Options = options;
            _network = new SemaphoreSlim(options.NetworkOperations, options.NetworkOperations);
            _backgroundNetwork = new SemaphoreSlim(options.BackgroundNetworkOperations, options.BackgroundNetworkOperations);
            _disk = new SemaphoreSlim(options.DiskOperations, options.DiskOperations);
            _folderSize = new SemaphoreSlim(options.FolderSizeOperations, options.FolderSizeOperations);
        }

        public ScanConcurrencyOptions Options { get; }

        public async Task RunAsBackgroundAsync(Func<Task> work)
        {
            _backgroundDepth.Value++;
            try
            {
                await work().ConfigureAwait(false);
            }
            finally
            {
                _backgroundDepth.Value--;
            }
        }

        public Task<T> RunNetworkAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
            _backgroundDepth.Value > 0
                ? RunBackgroundNetworkAsync(work, cancellationToken)
                : RunForegroundNetworkAsync(work, cancellationToken);

        public Task RunNetworkAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
            _backgroundDepth.Value > 0
                ? RunBackgroundNetworkAsync(work, cancellationToken)
                : RunForegroundNetworkAsync(work, cancellationToken);

        public Task<T> RunDiskAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
            RunAsync(_disk, work, cancellationToken);

        public async Task<T> RunFolderSizeAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
        {
            await _folderSize.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunAsync(_disk, work, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _folderSize.Release();
            }
        }

        private static async Task<T> RunAsync<T>(SemaphoreSlim gate, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task RunAsync(SemaphoreSlim gate, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<T> RunForegroundNetworkAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            EnterForeground();
            try
            {
                await _network.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await work(cancellationToken).ConfigureAwait(false); }
                finally { _network.Release(); }
            }
            finally
            {
                ExitForeground();
            }
        }

        private async Task RunForegroundNetworkAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            await RunForegroundNetworkAsync(async ct =>
            {
                await work(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> RunBackgroundNetworkAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            await WaitForForegroundIdleAsync(cancellationToken).ConfigureAwait(false);
            await _backgroundNetwork.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WaitForForegroundIdleAsync(cancellationToken).ConfigureAwait(false);
                await _network.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await work(cancellationToken).ConfigureAwait(false); }
                finally
                {
                    _network.Release();
                    await Task.Delay(Options.BackgroundRequestDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _backgroundNetwork.Release();
            }
        }

        private async Task RunBackgroundNetworkAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            await RunBackgroundNetworkAsync(async ct =>
            {
                await work(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private void EnterForeground()
        {
            if (Interlocked.Increment(ref _foregroundDemand) != 1)
                return;
            lock (_foregroundLock)
                _foregroundIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void ExitForeground()
        {
            if (Interlocked.Decrement(ref _foregroundDemand) != 0)
                return;
            lock (_foregroundLock)
                _foregroundIdle.TrySetResult(true);
        }

        private async Task WaitForForegroundIdleAsync(CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _foregroundDemand) > 0)
            {
                Task idleTask;
                lock (_foregroundLock)
                    idleTask = _foregroundIdle.Task;
                await idleTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult(true);
            return signal;
        }
    }
}
