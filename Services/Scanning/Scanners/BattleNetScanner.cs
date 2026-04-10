using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// Battle.net launcher integration
    /// </summary>
    public class BattleNetScanner : PlatformScanner
    {
        public override string PlatformName => "Battle.net";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            // TODO: Implement SQLite database query for product.db
            Debug.WriteLine("Battle.net scanner not yet implemented");
            return Task.FromResult(new List<GameCandidate>());
        }
    }
}
