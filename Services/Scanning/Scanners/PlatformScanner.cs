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
        int? SteamAppId = null
    );

    /// <summary>
    /// Base class for platform-specific game scanners
    /// </summary>
    public abstract class PlatformScanner
    {
        public abstract string PlatformName { get; }
        public abstract Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null);
    }
}
