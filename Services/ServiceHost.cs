using Codec.Services.Fetching;
using Codec.Services.Resolving;
using Codec.Services.Storage;

namespace Codec.Services
{
    /// <summary>
    /// Composition root that wires all service instances together.
    /// Replaces static service calls with explicit dependency graph.
    /// </summary>
    public sealed class ServiceHost
    {
        // Storage
        public MetadataCache Cache { get; }
        public LibraryStorageService LibraryStorage { get; }

        // Resolving
        public GameDetailsService GameDetails { get; }
        public GameNameService GameName { get; }

        // Fetching
        public SteamDetailsService SteamDetails { get; }
        public RawgDetailsService RawgDetails { get; }
        public HltbService Hltb { get; }
        public GameAssetService GameAssets { get; }
        public GridDbService GridDb { get; }

        public ServiceHost()
        {
            Cache = new MetadataCache();
            LibraryStorage = new LibraryStorageService();

            GameDetails = new GameDetailsService(Cache);
            GameName = new GameNameService(GameDetails);

            SteamDetails = new SteamDetailsService(Cache);
            RawgDetails = new RawgDetailsService(Cache);
            Hltb = new HltbService(Cache);
            GameAssets = new GameAssetService();
            GridDb = new GridDbService(GameAssets);
        }
    }
}
