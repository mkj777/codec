using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Represents a discovered game candidate before validation
    /// </summary>
    public record GameCandidate(
        string Name,
        string FolderPath,
        string Source,
        int? SteamAppId = null,
        string? EpicAppId = null,
        string? LaunchScriptPath = null,
        string? ExecutableHintPath = null,
        bool HasStrongGameSignals = false
    );

    /// <summary>
    /// Base class for platform-specific game scanners
    /// </summary>
    public abstract class PlatformScanner
    {
        protected readonly List<string> _knownLibraryPaths = new();

        public abstract string PlatformName { get; }
        public abstract Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null);

        /// <summary>
        /// Library root paths and individual game install dirs owned by this scanner.
        /// Populated after ScanAsync completes. HeuristicScanner uses these to skip paths
        /// already covered by a dedicated platform scanner.
        /// </summary>
        public IReadOnlyList<string> KnownLibraryPaths => _knownLibraryPaths;
    }
}
