using Codec.Services.Fetching;
using Codec.Services.Importing;
using Codec.Services.Resolving;
using Codec.Services.Storage;
using Codec.Services.Scanning;
using Codec.Services.Steam;

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
        public AppResetService AppReset { get; }

        // Resolving
        public GameDetailsService GameDetails { get; }
        public GameNameService GameName { get; }

        // Fetching
        public SteamKitService SteamKit { get; }
        public SteamAuthService SteamAuth { get; }
        public SteamLibraryService SteamLibrary { get; }
        public SteamDetailsService SteamDetails { get; }
        public RawgDetailsService RawgDetails { get; }
        public IgdbService Igdb { get; }
        public HltbService Hltb { get; }
        public GameAssetService GameAssets { get; }
        public GridDbService GridDb { get; }
        public DisplayedAssetService DisplayedAssets { get; }
        public GameImportPipeline GameImportPipeline { get; }
        public UpdateService Updates { get; }
        public ScanConcurrencyOptions ScanConcurrency { get; }
        public ScanResourceLimiter ScanResources { get; }
        public HeuristicInstallStateService HeuristicInstallState { get; }

        public ServiceHost()
        {
            ScanConcurrency = ScanConcurrencyOptions.CreateAdaptive();
            ScanResources = new ScanResourceLimiter(ScanConcurrency);
            HeuristicInstallState = new HeuristicInstallStateService(ScanResources);
            Cache = new MetadataCache(ScanResources);
            LibraryStorage = new LibraryStorageService();
            AppSettings = new AppSettingsService();
            AppReset = new AppResetService();

            GameDetails = new GameDetailsService(Cache);
            GameName = new GameNameService(GameDetails, maxConcurrentApiRequests: 32);

            SteamKit = new SteamKitService();
            SteamAuth = new SteamAuthService();
            SteamLibrary = new SteamLibraryService(SteamAuth);
            SteamDetails = new SteamDetailsService(Cache, SteamKit, ScanResources);
            RawgDetails = new RawgDetailsService(Cache);
            Igdb = new IgdbService(new System.Net.Http.HttpClient(), ScanResources);
            Hltb = new HltbService(Cache);
            GameAssets = new GameAssetService(SteamKit, ScanResources);
            GridDb = new GridDbService(GameAssets, ScanResources);
            DisplayedAssets = new DisplayedAssetService(GameAssets, GridDb, RawgDetails);
            GameImportPipeline = new GameImportPipeline(GameName, GameDetails, SteamDetails, RawgDetails, Igdb, Hltb, DisplayedAssets, ScanResources);
            Updates = new UpdateService();
        }
    }
}
