using Codec.Helpers;
using Codec.Models;
using Codec.Services.Storage;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Scanning
{
    public readonly record struct HeuristicInstallState(bool IsInstalled, long? InstalledSize);

    public sealed class HeuristicInstallStateService
    {
        private const long MaximumUninstallRemainderBytes = 500L * 1024L * 1024L;
        private readonly ScanResourceLimiter _resourceLimiter;

        public HeuristicInstallStateService(ScanResourceLimiter resourceLimiter) => _resourceLimiter = resourceLimiter;

        public async Task<HeuristicInstallState?> EvaluateAsync(Game game, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(PlatformSourceNames.NormalizeImportSource(game.ImportedFrom), PlatformSourceNames.HeuristicScan, StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.IsNullOrWhiteSpace(game.FolderLocation) || !Directory.Exists(game.FolderLocation))
                return new HeuristicInstallState(false, null);

            FolderSizeResult result = await _resourceLimiter.RunFolderSizeAsync(
                ct => FolderSizeService.TryCalculateAsync(game.FolderLocation, ct), cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return null;

            bool isDrasticallyReduced = game.FolderSize > 0 &&
                result.Size <= MaximumUninstallRemainderBytes &&
                result.Size <= game.FolderSize / 10;
            bool isInstalled = result.Size > 0 && !isDrasticallyReduced;
            return new HeuristicInstallState(isInstalled, isInstalled ? result.Size : null);
        }
    }
}
