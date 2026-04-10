using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Codec.Models
{
    public partial class Game : ObservableObject
    {
        private static readonly Dictionary<string, int> PlatformDisplayOrder = new(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = 0,
            ["playstation"] = 1,
            ["xbox"] = 2,
            ["nintendo-switch"] = 3,
            ["macos"] = 4,
            ["linux"] = 5,
            ["ios"] = 6,
            ["android"] = 7
        };

        [SetsRequiredMembers]
        public Game()
        {
            // Initialize “required” strings to non-null defaults
            Name = string.Empty;
            Executable = string.Empty;
            FolderLocation = string.Empty;
            ImportedFrom = string.Empty;
        }

        // basic information
        [ObservableProperty] private Guid id = Guid.NewGuid();
        [ObservableProperty] private DateTime dateAdded = DateTime.Now;

        [ObservableProperty] private string executable;
        [ObservableProperty] private string folderLocation;
        [ObservableProperty] private long folderSize;
        [ObservableProperty] private string importedFrom;

        // Display-only property that shows just the platform name without the path
        public string ImportedFromDisplay =>
            ImportedFrom.StartsWith("Steam", StringComparison.OrdinalIgnoreCase) ? "Steam" : ImportedFrom;

        // external IDs
        [ObservableProperty] private int? steamID;
        [ObservableProperty] private int? rawgID;
        [ObservableProperty] private string? rawgSlug;
        [ObservableProperty] private int? gridDbId;

        // game details
        [ObservableProperty] private string name;
        [ObservableProperty] private string? publisher;
        [ObservableProperty] private string? developer;
        [ObservableProperty] private List<string>? genres;
        [ObservableProperty] private List<string>? categories;
        [ObservableProperty] private string? price;
        [ObservableProperty] private string? priceDiscount;
        [ObservableProperty] private string? description;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlatformLogoUris))]
        private List<string>? platforms;

        [ObservableProperty] private DateTime? releaseDate;
        [ObservableProperty] private double? steamRating;
        [ObservableProperty] private string? steamReviewSummary;
        [ObservableProperty] private int? steamReviewTotal;
        [ObservableProperty] private string? ageRating;
        [ObservableProperty] private int? timeToCompleteMainStory;
        [ObservableProperty] private int? timeToCompleteCompletionist;
        [ObservableProperty] private bool isFullyImported;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private bool hasHeroAssetSource;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private bool hasLogoAssetSource;

        public IEnumerable<string> PlatformLogoUris => (Platforms ?? Enumerable.Empty<string>())
            .Select(GetPlatformLogo)
            .Where(platform => platform is not null)
            .DistinctBy(platform => platform!.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(platform => platform!.Order)
            .Select(platform => platform!.LogoUri);

        // game assets with cache for offline first, effective path resolution
        private static string GetEffectiveAssetPath(string? cachePath, string? url, string placeholder)
        {
            if (TryGetLocalAssetUri(cachePath, out var localAssetUri))
            {
                return localAssetUri;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return placeholder;
        }

        private static bool TryGetLocalAssetUri(string? cachePath, out string localAssetUri)
        {
            localAssetUri = string.Empty;
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return false;
            }

            try
            {
                if (Uri.TryCreate(cachePath, UriKind.Absolute, out var parsed) && parsed.IsFile)
                {
                    if (File.Exists(parsed.LocalPath))
                    {
                        localAssetUri = parsed.AbsoluteUri;
                        return true;
                    }

                    return false;
                }

                if (File.Exists(cachePath))
                {
                    localAssetUri = new Uri(cachePath).AbsoluteUri;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
        // placeholders
        private const string PlaceholderCapsule = "https://placehold.co/600x900/1c1c1c/ffffff?text=Capsule";
        private const string PlaceholderHero = "https://placehold.co/1920x620/1c1c1c/ffffff?text=Hero";
        private const string PlaceholderLogo = "https://placehold.co/1280x260/1c1c1c/ffffff?text=Logo";

        // capsule
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibCapsule))] private string? libCapsuleUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibCapsule))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private string? libCapsuleCache;
        public string LibCapsule => GetEffectiveAssetPath(LibCapsuleCache, LibCapsuleUrl, PlaceholderCapsule);

        // hero
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibHero))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private string? libHeroUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibHero))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private string? libHeroCache;
        public string LibHero => GetEffectiveAssetPath(LibHeroCache, LibHeroUrl, PlaceholderHero);

        // logo
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibLogo))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private string? libLogoUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibLogo))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        private string? libLogoCache;
        public string LibLogo => GetEffectiveAssetPath(LibLogoCache, LibLogoUrl, PlaceholderLogo);
        public bool DisplayedAssetsReady => HasRequiredDisplayedAssetsCached();

        // media
        [ObservableProperty]
        private List<string> media = new()
        {
            "https://placehold.co/1920x1080/1c1c1c/ffffff?text=Media+1",
            "https://placehold.co/1920x1080/1c1c1c/ffffff?text=Media+2",
            "https://placehold.co/1920x1080/1c1c1c/ffffff?text=Media+3"
        };

        // external links
        [ObservableProperty] private string? officialWebsiteUrl;
        [ObservableProperty] private string? steamPageUrl;
        [ObservableProperty] private string? rawgUrl;
        [ObservableProperty] private string? hltbUrl;

        // Maps a raw platform string to a canonical key, asset, and fixed display order.
        private static PlatformLogoInfo? GetPlatformLogo(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                return null;
            }

            var normalized = platform.Trim().ToLowerInvariant();

            if (normalized.Contains("pc") || normalized.Contains("windows"))
            {
                return CreatePlatformLogo("windows", "ms-appx:///Assets/windows_logo.png");
            }

            if (normalized.Contains("playstation"))
            {
                return CreatePlatformLogo("playstation", "ms-appx:///Assets/playstation_logo.png");
            }

            if (normalized.Contains("xbox"))
            {
                return CreatePlatformLogo("xbox", "ms-appx:///Assets/xbox_logo.png");
            }

            if (normalized.Contains("nintendo") || normalized.Contains("switch"))
            {
                return CreatePlatformLogo("nintendo-switch", "ms-appx:///Assets/NintendoSwitch_logo.png");
            }

            if (normalized.Contains("mac"))
            {
                return CreatePlatformLogo("macos", "ms-appx:///Assets/MacOS_logo.png");
            }

            if (normalized.Contains("linux"))
            {
                return CreatePlatformLogo("linux", "ms-appx:///Assets/linux_logo.png");
            }

            if (normalized.Contains("ios"))
            {
                return CreatePlatformLogo("ios", "ms-appx:///Assets/iOS_logo.png");
            }

            if (normalized.Contains("android"))
            {
                return CreatePlatformLogo("android", "ms-appx:///Assets/android_logo.png");
            }

            return null;
        }

        private static PlatformLogoInfo CreatePlatformLogo(string key, string logoUri)
            => new(key, logoUri, PlatformDisplayOrder[key]);

        private sealed record PlatformLogoInfo(string Key, string LogoUri, int Order);

        [ObservableProperty] private string? _launchScript;

        private bool HasRequiredDisplayedAssetsCached()
        {
            bool hasCover = HasLocalAsset(LibCapsuleCache);
            bool hasHero = HasLocalAsset(LibHeroCache);
            bool hasLogo = !HasLogoAssetSource || HasLocalAsset(LibLogoCache);
            return hasCover && hasHero && hasLogo;
        }

        private static bool HasLocalAsset(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var parsed) && parsed.IsFile)
                {
                    return File.Exists(parsed.LocalPath);
                }

                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public Game CreateHydrationSnapshot()
        {
            return new Game
            {
                Id = Id,
                DateAdded = DateAdded,
                Executable = Executable,
                FolderLocation = FolderLocation,
                FolderSize = FolderSize,
                ImportedFrom = ImportedFrom,
                SteamID = SteamID,
                RawgID = RawgID,
                RawgSlug = RawgSlug,
                GridDbId = GridDbId,
                Name = Name,
                Publisher = Publisher,
                Developer = Developer,
                Genres = Genres == null ? null : new List<string>(Genres),
                Categories = Categories == null ? null : new List<string>(Categories),
                Price = Price,
                PriceDiscount = PriceDiscount,
                Description = Description,
                Platforms = Platforms == null ? null : new List<string>(Platforms),
                ReleaseDate = ReleaseDate,
                SteamRating = SteamRating,
                SteamReviewSummary = SteamReviewSummary,
                SteamReviewTotal = SteamReviewTotal,
                AgeRating = AgeRating,
                TimeToCompleteMainStory = TimeToCompleteMainStory,
                TimeToCompleteCompletionist = TimeToCompleteCompletionist,
                IsFullyImported = IsFullyImported,
                HasHeroAssetSource = HasHeroAssetSource,
                HasLogoAssetSource = HasLogoAssetSource,
                LibCapsuleUrl = LibCapsuleUrl,
                LibCapsuleCache = LibCapsuleCache,
                LibHeroUrl = LibHeroUrl,
                LibHeroCache = LibHeroCache,
                LibLogoUrl = LibLogoUrl,
                LibLogoCache = LibLogoCache,
                Media = new List<string>(Media),
                OfficialWebsiteUrl = OfficialWebsiteUrl,
                SteamPageUrl = SteamPageUrl,
                RawgUrl = RawgUrl,
                HltbUrl = HltbUrl,
                LaunchScript = LaunchScript
            };
        }

        public void ApplyHydrationSnapshot(Game source)
        {
            if (source == null)
            {
                return;
            }

            DateAdded = source.DateAdded;
            Executable = source.Executable;
            FolderLocation = source.FolderLocation;
            FolderSize = source.FolderSize;
            ImportedFrom = source.ImportedFrom;
            SteamID = source.SteamID;
            RawgID = source.RawgID;
            RawgSlug = source.RawgSlug;
            GridDbId = source.GridDbId;
            Name = source.Name;
            Publisher = source.Publisher;
            Developer = source.Developer;
            Genres = source.Genres == null ? null : new List<string>(source.Genres);
            Categories = source.Categories == null ? null : new List<string>(source.Categories);
            Price = source.Price;
            PriceDiscount = source.PriceDiscount;
            Description = source.Description;
            Platforms = source.Platforms == null ? null : new List<string>(source.Platforms);
            ReleaseDate = source.ReleaseDate;
            SteamRating = source.SteamRating;
            SteamReviewSummary = source.SteamReviewSummary;
            SteamReviewTotal = source.SteamReviewTotal;
            AgeRating = source.AgeRating;
            TimeToCompleteMainStory = source.TimeToCompleteMainStory;
            TimeToCompleteCompletionist = source.TimeToCompleteCompletionist;
            HasHeroAssetSource = source.HasHeroAssetSource;
            HasLogoAssetSource = source.HasLogoAssetSource;
            LibCapsuleUrl = source.LibCapsuleUrl;
            LibCapsuleCache = source.LibCapsuleCache;
            LibHeroUrl = source.LibHeroUrl;
            LibHeroCache = source.LibHeroCache;
            LibLogoUrl = source.LibLogoUrl;
            LibLogoCache = source.LibLogoCache;
            Media = new List<string>(source.Media);
            OfficialWebsiteUrl = source.OfficialWebsiteUrl;
            SteamPageUrl = source.SteamPageUrl;
            RawgUrl = source.RawgUrl;
            HltbUrl = source.HltbUrl;
            LaunchScript = source.LaunchScript;
            IsFullyImported = source.IsFullyImported;
            NotifyDisplayedAssetStateChanged();
        }

        public void NotifyDisplayedAssetStateChanged()
        {
            OnPropertyChanged(nameof(LibCapsule));
            OnPropertyChanged(nameof(LibHero));
            OnPropertyChanged(nameof(LibLogo));
            OnPropertyChanged(nameof(DisplayedAssetsReady));
        }
    }
}
