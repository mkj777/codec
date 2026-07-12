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
        public static ScanConcurrencyOptions CreateAdaptive(int? processorCount = null)
        {
            int processors = Math.Max(1, processorCount ?? Environment.ProcessorCount);
            int validationWorkers = Math.Clamp(processors / 4, 2, 4);
            int importWorkers = Math.Clamp(processors / 4, 2, 4);

            return new ScanConcurrencyOptions(
                HeuristicWorkers: Math.Clamp(processors / 2, 2, 6),
                ValidationWorkers: validationWorkers,
                ImportWorkers: importWorkers,
                NetworkOperations: Math.Clamp(processors / 3, 2, 6),
                DiskOperations: Math.Clamp(processors / 4, 1, 4),
                FolderSizeOperations: Math.Clamp(processors / 8, 1, 2),
                ImportQueueCapacity: Math.Max(8, importWorkers * 4),
                ValidationBufferCapacity: Math.Max(4, validationWorkers * 2));
        }
    }

    public sealed class ScanResourceLimiter
    {
        private readonly SemaphoreSlim _network;
        private readonly SemaphoreSlim _disk;
        private readonly SemaphoreSlim _folderSize;

        public ScanResourceLimiter(ScanConcurrencyOptions options)
        {
            Options = options;
            _network = new SemaphoreSlim(options.NetworkOperations, options.NetworkOperations);
            _disk = new SemaphoreSlim(options.DiskOperations, options.DiskOperations);
            _folderSize = new SemaphoreSlim(options.FolderSizeOperations, options.FolderSizeOperations);
        }

        public ScanConcurrencyOptions Options { get; }

        public Task<T> RunNetworkAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
            RunAsync(_network, work, cancellationToken);

        public Task RunNetworkAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
            RunAsync(_network, work, cancellationToken);

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
    }
}
