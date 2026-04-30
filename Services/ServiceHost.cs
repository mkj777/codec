using Codec.Services.Fetching;
using Codec.Services.Importing;
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
        public AppSettingsService AppSettings { get; }

        // Resolving
        public GameDetailsService GameDetails { get; }
        public GameNameService GameName { get; }

        // Fetching
        public SteamKitService SteamKit { get; }
        public SteamDetailsService SteamDetails { get; }
        public RawgDetailsService RawgDetails { get; }
        public IgdbService Igdb { get; }
        public HltbService Hltb { get; }
        public GameAssetService GameAssets { get; }
        public GridDbService GridDb { get; }
        public DisplayedAssetService DisplayedAssets { get; }
        public GameImportPipeline GameImportPipeline { get; }
        public UpdateService Updates { get; }

        public ServiceHost()
        {
            Cache = new MetadataCache();
            LibraryStorage = new LibraryStorageService();
            AppSettings = new AppSettingsService();

            GameDetails = new GameDetailsService(Cache);
            GameName = new GameNameService(GameDetails);

            SteamKit = new SteamKitService();
            SteamDetails = new SteamDetailsService(Cache, SteamKit);
            RawgDetails = new RawgDetailsService(Cache);
            Igdb = new IgdbService();
            Hltb = new HltbService(Cache);
            GameAssets = new GameAssetService(SteamKit);
            GridDb = new GridDbService(GameAssets);
            DisplayedAssets = new DisplayedAssetService(GameAssets, GridDb, RawgDetails);
            GameImportPipeline = new GameImportPipeline(GameName, GameDetails, SteamDetails, RawgDetails, Igdb, Hltb, DisplayedAssets);
            Updates = new UpdateService();
        }
    }
}
