using CommunityToolkit.Mvvm.ComponentModel;
using Codec.Helpers;
using Codec.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Codec.Models
{
    public partial class Game : ObservableObject
    {
        private const string NotAvailableText = "Not Available";
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
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DateAddedDisplay))]
        private DateTime dateAdded = DateTime.Now;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        private string executable;
        [ObservableProperty] private string folderLocation;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FolderSizeDisplay))]
        private long folderSize;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FavoriteGlyph))]
        [NotifyPropertyChangedFor(nameof(FavoriteToolTip))]
        [NotifyPropertyChangedFor(nameof(FavoriteOpacity))]
        private bool isFavorite;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ImportedFromDisplay))]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        [NotifyPropertyChangedFor(nameof(IsAlsoOwnedOnSteam))]
        private string importedFrom;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOwnedOnly))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        [NotifyPropertyChangedFor(nameof(CanInstall))]
        [NotifyPropertyChangedFor(nameof(LibraryCardOpacity))]
        [NotifyPropertyChangedFor(nameof(SidebarOpacity))]
        private bool isInstalled = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOwnedOnly))]
        [NotifyPropertyChangedFor(nameof(CanInstall))]
        [NotifyPropertyChangedFor(nameof(LibraryCardOpacity))]
        [NotifyPropertyChangedFor(nameof(SidebarOpacity))]
        [NotifyPropertyChangedFor(nameof(IsAlsoOwnedOnSteam))]
        private bool isSteamOwned;

        [ObservableProperty] private string? steamAppType;

        // Display-only property that shows just the platform name without the path
        public string ImportedFromDisplay
        {
            get
            {
                string normalized = PlatformSourceNames.NormalizeImportSource(ImportedFrom);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return string.Empty;
                }

                return normalized.StartsWith(PlatformSourceNames.Steam, StringComparison.OrdinalIgnoreCase)
                    ? PlatformSourceNames.Steam
                    : normalized;
            }
        }

        // external IDs
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        [NotifyPropertyChangedFor(nameof(EffectiveSteamMetadataAppId))]
        [NotifyPropertyChangedFor(nameof(UsesAlternateMetadataLookupName))]
        [NotifyPropertyChangedFor(nameof(IsAlsoOwnedOnSteam))]
        private int? steamID;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveSteamMetadataAppId))]
        private int? steamMetadataAppId;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        private string? epicAppId;
        [ObservableProperty] private int? rawgID;
        [ObservableProperty] private string? rawgSlug;
        [property: JsonPropertyName("IgdbId")]
        [ObservableProperty] private int? igdbId;
        [ObservableProperty] private int? gridDbId;

        // game details
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveMetadataLookupName))]
        [NotifyPropertyChangedFor(nameof(EffectiveSteamMetadataAppId))]
        [NotifyPropertyChangedFor(nameof(UsesAlternateMetadataLookupName))]
        private string name;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveMetadataLookupName))]
        [NotifyPropertyChangedFor(nameof(EffectiveSteamMetadataAppId))]
        [NotifyPropertyChangedFor(nameof(UsesAlternateMetadataLookupName))]
        private string? metadataLookupName;
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
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOriginalReleaseDate))]
        [NotifyPropertyChangedFor(nameof(OriginalReleaseDisplay))]
        private DateTime? originalReleaseDate;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OriginalReleaseDisplay))]
        private string? originalGameName;
        [ObservableProperty] private int? igdbVersionParentId;
        [ObservableProperty] private int? igdbCategory;
        [ObservableProperty] private string? igdbCategoryName;
        [ObservableProperty] private string? franchiseName;
        [ObservableProperty] private int? igdbFranchiseId;
        [ObservableProperty] private List<FranchiseGameRef>? franchiseGames;
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

        public IEnumerable<string> PlatformLogoUris => GetPlatformLogoUris(Platforms);

        public static IEnumerable<string> GetPlatformLogoUris(IEnumerable<string>? platforms)
            => (platforms ?? Enumerable.Empty<string>())
                .Select(GetPlatformLogo)
                .Where(p => p is not null)
                .DistinctBy(p => p!.Key, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p!.Order)
                .Select(p => p!.LogoUri);

        public string DisplayAgeRating => IsUnavailableText(AgeRating, "Not Rated")
            ? NotAvailableText
            : AgeRating!;

        public double AgeRatingOpacity => GetAvailabilityOpacity(!IsUnavailableText(AgeRating, "Not Rated"));

        public string DisplaySteamReview => HasSteamReview
            ? $"{SteamReviewSummary} ({SteamReviewTotal})"
            : NotAvailableText;

        public string DisplaySteamReviewSummary => HasSteamReview
            ? SteamReviewSummary!
            : NotAvailableText;

        public string DisplaySteamReviewCount => HasSteamReview
            ? $" ({SteamReviewTotal})"
            : string.Empty;

        public double SteamReviewOpacity => GetAvailabilityOpacity(HasSteamReview);

        public string DisplayMainStory => FormatCompletionTime(TimeToCompleteMainStory);

        public double MainStoryOpacity => GetAvailabilityOpacity(HasCompletionTime(TimeToCompleteMainStory));

        public string DisplayCompletionist => FormatCompletionTime(TimeToCompleteCompletionist);

        public double CompletionistOpacity => GetAvailabilityOpacity(HasCompletionTime(TimeToCompleteCompletionist));

        public bool IsRemakeOrRemaster => IgdbCategory is 8 or 9;

        [JsonIgnore]
        public int? EffectiveSteamMetadataAppId => SteamMetadataAppId ?? (UsesAlternateMetadataLookupName ? null : SteamID);

        [JsonIgnore]
        public string EffectiveMetadataLookupName => GameNameCleaner.GetMetadataLookupName(Name, MetadataLookupName);

        [JsonIgnore]
        public bool UsesAlternateMetadataLookupName
        {
            get
            {
                string displayName = GameNameCleaner.RemoveTrailingDomainTag(Name);
                string metadataName = EffectiveMetadataLookupName;
                return !string.IsNullOrWhiteSpace(metadataName) &&
                       !string.Equals(displayName, metadataName, StringComparison.OrdinalIgnoreCase);
            }
        }

        [JsonIgnore]
        public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

        [JsonIgnore]
        public string FavoriteToolTip => IsFavorite ? "Remove from favorites" : "Add to favorites";

        [JsonIgnore]
        public double FavoriteOpacity => IsFavorite ? 1d : 0.62d;

        public bool HasOriginalReleaseDate => OriginalReleaseDate.HasValue;

        public string OriginalReleaseDisplay
        {
            get
            {
                if (!OriginalReleaseDate.HasValue)
                {
                    return string.Empty;
                }

                string year = OriginalReleaseDate.Value.Year.ToString();
                return $"Originally released: {year}";
            }
        }

        // game assets with cache for offline first, effective path resolution
        private static string? GetEffectiveAssetPath(string? cachePath, string? url, string? placeholderRelativePath = null)
            => AssetUriResolver.ResolveImageSource(cachePath, url, placeholderRelativePath);
        // library_capsule
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibraryCapsule))]
        [property: JsonPropertyName("LibCapsuleUrl")]
        private string? libraryCapsuleUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibraryCapsule))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        [property: JsonPropertyName("LibCapsuleCache")]
        private string? libraryCapsuleCache;
        private const string PlaceholderLibraryCapsuleRelativePath = "Assets/noCover.png";
        [JsonIgnore]
        public string LibraryCapsule => GetEffectiveAssetPath(LibraryCapsuleCache, LibraryCapsuleUrl, PlaceholderLibraryCapsuleRelativePath)!;

        // library_hero
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibraryHero))]
        [property: JsonPropertyName("LibHeroUrl")]
        private string? libraryHeroUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibraryHero))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        [property: JsonPropertyName("LibHeroCache")]
        private string? libraryHeroCache;
        [JsonIgnore]
        public string? LibraryHero => GetEffectiveAssetPath(LibraryHeroCache, LibraryHeroUrl);

        // library_logo
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibraryLogo))]
        [NotifyPropertyChangedFor(nameof(HasLogo))]
        [property: JsonPropertyName("LibLogoUrl")]
        private string? libraryLogoUrl;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LibraryLogo))]
        [NotifyPropertyChangedFor(nameof(HasLogo))]
        [NotifyPropertyChangedFor(nameof(DisplayedAssetsReady))]
        [property: JsonPropertyName("LibLogoCache")]
        private string? libraryLogoCache;
        [JsonIgnore]
        public string? LibraryLogo => GetEffectiveAssetPath(LibraryLogoCache, LibraryLogoUrl);
        [JsonIgnore]
        public bool HasLogo => !string.IsNullOrWhiteSpace(LibraryLogoUrl) || !string.IsNullOrWhiteSpace(LibraryLogoCache);
        public bool DisplayedAssetsReady => HasRequiredDisplayedAssetsCached();

        // media
        [ObservableProperty]
        private List<string> media = new();

        // external links
        [ObservableProperty] private string? officialWebsiteUrl;
        [ObservableProperty] private string? steamPageUrl;
        [ObservableProperty] private string? rawgUrl;
        [ObservableProperty] private string? igdbUrl;
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
                return CreatePlatformLogo("windows", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/windows_logo.png"));
            }

            if (normalized.Contains("playstation"))
            {
                return CreatePlatformLogo("playstation", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/playstation_logo.png"));
            }

            if (normalized.Contains("xbox"))
            {
                return CreatePlatformLogo("xbox", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/xbox_logo.png"));
            }

            if (normalized.Contains("nintendo") || normalized.Contains("switch"))
            {
                return CreatePlatformLogo("nintendo-switch", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/NintendoSwitch_logo.png"));
            }

            if (normalized.Contains("mac"))
            {
                return CreatePlatformLogo("macos", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/MacOS_logo.png"));
            }

            if (normalized.Contains("linux"))
            {
                return CreatePlatformLogo("linux", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/linux_logo.png"));
            }

            if (normalized.Contains("ios"))
            {
                return CreatePlatformLogo("ios", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/iOS_logo.png"));
            }

            if (normalized.Contains("android"))
            {
                return CreatePlatformLogo("android", AssetUriResolver.ResolveBundledAssetUri("Assets/Platforms/android_logo.png"));
            }

            return null;
        }

        private static PlatformLogoInfo CreatePlatformLogo(string key, string logoUri)
            => new(key, logoUri, PlatformDisplayOrder[key]);

        private sealed record PlatformLogoInfo(string Key, string LogoUri, int Order);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(HasCustomLaunchScript))]
        [NotifyPropertyChangedFor(nameof(HasCustomLaunchOptions))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        private string? _launchScript;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(HasCustomLaunchScript))]
        [NotifyPropertyChangedFor(nameof(HasCustomLaunchOptions))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        private bool _useLaunchScriptOverride;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LaunchOptionsDisplay))]
        [NotifyPropertyChangedFor(nameof(HasCustomLaunchOptions))]
        [NotifyPropertyChangedFor(nameof(CanLaunch))]
        private bool _useExecutableOverride;

        [JsonIgnore]
        public bool HasCustomLaunchScript =>
            !string.IsNullOrWhiteSpace(LaunchScript) &&
            (UseLaunchScriptOverride || !IsPlatformLaunchScriptTarget);

        [JsonIgnore]
        public bool HasCustomLaunchOptions => HasCustomLaunchScript || UseExecutableOverride;

        [JsonIgnore]
        public bool CanLaunch
        {
            get
            {
                if (HasCustomLaunchScript && HasExistingFile(LaunchScript))
                {
                    return true;
                }

                if (UseExecutableOverride && HasExistingFile(Executable))
                {
                    return true;
                }

                if ((IsSteamLaunchTarget && IsInstalled) || IsEpicLaunchTarget)
                {
                    return true;
                }

                if (IsRiotLaunchTarget && HasExistingFile(LaunchScript))
                {
                    return true;
                }

                return HasExistingFile(Executable);
            }
        }

        [JsonIgnore]
        public string LaunchOptionsDisplay
        {
            get
            {
                if (HasCustomLaunchScript)
                {
                    return LaunchScript!;
                }

                if (UseExecutableOverride && !string.IsNullOrWhiteSpace(Executable))
                {
                    return Executable;
                }

                if (IsSteamLaunchTarget)
                {
                    return "Launches through Steam";
                }

                if (IsEpicLaunchTarget)
                {
                    return "Launches through Epic Games";
                }

                if (IsRiotLaunchTarget)
                {
                    return "Launches through Riot Games";
                }

                return !string.IsNullOrWhiteSpace(Executable)
                    ? Executable
                    : "No launch target selected";
            }
        }

        [JsonIgnore]
        public bool IsSteamLaunchTarget =>
            SteamID.HasValue &&
            !string.IsNullOrWhiteSpace(ImportedFrom) &&
            ImportedFrom.StartsWith("Steam", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsAlsoOwnedOnSteam => IsSteamOwned && SteamID.HasValue && !IsSteamLaunchTarget;

        [JsonIgnore]
        public bool IsOwnedOnly => IsSteamOwned && !IsInstalled;

        [JsonIgnore]
        public bool CanInstall => IsOwnedOnly && SteamID.HasValue;

        [JsonIgnore]
        public double LibraryCardOpacity => IsOwnedOnly ? 0.68d : 1d;

        [JsonIgnore]
        public double SidebarOpacity => IsOwnedOnly ? 0.48d : 1d;

        [JsonIgnore]
        public bool IsEpicLaunchTarget =>
            !string.IsNullOrWhiteSpace(EpicAppId) &&
            PlatformSourceNames.IsEpicGames(ImportedFrom);

        [JsonIgnore]
        public bool IsRiotLaunchTarget =>
            !string.IsNullOrWhiteSpace(ImportedFrom) &&
            ImportedFrom.Equals("Riot Games", StringComparison.OrdinalIgnoreCase);

        private bool IsPlatformLaunchScriptTarget => IsRiotLaunchTarget;

        private static bool HasExistingFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                return File.Exists(filePath);
            }
            catch
            {
                return false;
            }
        }

        private bool HasRequiredDisplayedAssetsCached()
        {
            bool hasCover = HasLocalAsset(LibraryCapsuleCache);
            bool hasHero = HasLocalAsset(LibraryHeroCache);
            bool hasLogo = !HasLogoAssetSource || HasLocalAsset(LibraryLogoCache);
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

        partial void OnAgeRatingChanged(string? value)
        {
            OnPropertyChanged(nameof(DisplayAgeRating));
            OnPropertyChanged(nameof(AgeRatingOpacity));
        }

        partial void OnSteamReviewSummaryChanged(string? value)
        {
            OnPropertyChanged(nameof(DisplaySteamReview));
            OnPropertyChanged(nameof(DisplaySteamReviewSummary));
            OnPropertyChanged(nameof(DisplaySteamReviewCount));
            OnPropertyChanged(nameof(SteamReviewOpacity));
        }

        partial void OnSteamReviewTotalChanged(int? value)
        {
            OnPropertyChanged(nameof(DisplaySteamReview));
            OnPropertyChanged(nameof(DisplaySteamReviewSummary));
            OnPropertyChanged(nameof(DisplaySteamReviewCount));
            OnPropertyChanged(nameof(SteamReviewOpacity));
        }

        partial void OnTimeToCompleteMainStoryChanged(int? value)
        {
            OnPropertyChanged(nameof(DisplayMainStory));
            OnPropertyChanged(nameof(MainStoryOpacity));
        }

        partial void OnTimeToCompleteCompletionistChanged(int? value)
        {
            OnPropertyChanged(nameof(DisplayCompletionist));
            OnPropertyChanged(nameof(CompletionistOpacity));
        }

        private bool HasSteamReview =>
            !IsUnavailableText(SteamReviewSummary, "N/A") &&
            SteamReviewTotal is > 0;

        private static bool HasCompletionTime(int? seconds) => seconds is > 0;

        private static double GetAvailabilityOpacity(bool isAvailable) => isAvailable ? 1d : 0.6d;

        private static bool IsUnavailableText(string? value, params string[] placeholders)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            string normalized = value.Trim();
            if (normalized.Equals(NotAvailableText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string placeholder in placeholders)
            {
                if (normalized.Equals(placeholder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatCompletionTime(int? seconds)
        {
            if (seconds is not > 0)
            {
                return NotAvailableText;
            }

            double hours = seconds.Value / 3600d;
            double rounded = Math.Round(hours * 2, MidpointRounding.AwayFromZero) / 2d;

            if (rounded <= 0)
            {
                return NotAvailableText;
            }

            bool isHalfHour = Math.Abs(rounded - Math.Round(rounded)) > 0.001;
            if (!isHalfHour)
            {
                int whole = (int)Math.Round(rounded);
                return whole == 1 ? "1 Hour" : $"{whole} Hours";
            }

            int wholePart = (int)Math.Floor(rounded);
            return wholePart <= 0 ? "½ Hour" : $"{wholePart}½ Hours";
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
                IsFavorite = IsFavorite,
                ImportedFrom = ImportedFrom,
                IsInstalled = IsInstalled,
                IsSteamOwned = IsSteamOwned,
                SteamAppType = SteamAppType,
                SteamID = SteamID,
                SteamMetadataAppId = SteamMetadataAppId,
                EpicAppId = EpicAppId,
                RawgID = RawgID,
                RawgSlug = RawgSlug,
                IgdbId = IgdbId,
                GridDbId = GridDbId,
                Name = Name,
                MetadataLookupName = MetadataLookupName,
                Publisher = Publisher,
                Developer = Developer,
                Genres = Genres == null ? null : new List<string>(Genres),
                Categories = Categories == null ? null : new List<string>(Categories),
                Price = Price,
                PriceDiscount = PriceDiscount,
                Description = Description,
                Platforms = Platforms == null ? null : new List<string>(Platforms),
                ReleaseDate = ReleaseDate,
                OriginalReleaseDate = OriginalReleaseDate,
                OriginalGameName = OriginalGameName,
                IgdbVersionParentId = IgdbVersionParentId,
                IgdbCategory = IgdbCategory,
                IgdbCategoryName = IgdbCategoryName,
                FranchiseName = FranchiseName,
                IgdbFranchiseId = IgdbFranchiseId,
                FranchiseGames = FranchiseGames == null ? null : new List<FranchiseGameRef>(FranchiseGames),
                SteamRating = SteamRating,
                SteamReviewSummary = SteamReviewSummary,
                SteamReviewTotal = SteamReviewTotal,
                AgeRating = AgeRating,
                TimeToCompleteMainStory = TimeToCompleteMainStory,
                TimeToCompleteCompletionist = TimeToCompleteCompletionist,
                IsFullyImported = IsFullyImported,
                HasHeroAssetSource = HasHeroAssetSource,
                HasLogoAssetSource = HasLogoAssetSource,
                LibraryCapsuleUrl = LibraryCapsuleUrl,
                LibraryCapsuleCache = LibraryCapsuleCache,
                LibraryHeroUrl = LibraryHeroUrl,
                LibraryHeroCache = LibraryHeroCache,
                LibraryLogoUrl = LibraryLogoUrl,
                LibraryLogoCache = LibraryLogoCache,
                Media = new List<string>(Media),
                OfficialWebsiteUrl = OfficialWebsiteUrl,
                SteamPageUrl = SteamPageUrl,
                RawgUrl = RawgUrl,
                IgdbUrl = IgdbUrl,
                HltbUrl = HltbUrl,
                LaunchScript = LaunchScript,
                UseLaunchScriptOverride = UseLaunchScriptOverride,
                UseExecutableOverride = UseExecutableOverride
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
            IsFavorite = source.IsFavorite;
            ImportedFrom = source.ImportedFrom;
            IsInstalled = source.IsInstalled;
            IsSteamOwned = source.IsSteamOwned;
            SteamAppType = source.SteamAppType;
            SteamID = source.SteamID;
            SteamMetadataAppId = source.SteamMetadataAppId;
            EpicAppId = source.EpicAppId;
            RawgID = source.RawgID;
            RawgSlug = source.RawgSlug;
            IgdbId = source.IgdbId;
            GridDbId = source.GridDbId;
            Name = source.Name;
            MetadataLookupName = source.MetadataLookupName;
            Publisher = source.Publisher;
            Developer = source.Developer;
            Genres = source.Genres == null ? null : new List<string>(source.Genres);
            Categories = source.Categories == null ? null : new List<string>(source.Categories);
            Price = source.Price;
            PriceDiscount = source.PriceDiscount;
            Description = source.Description;
            Platforms = source.Platforms == null ? null : new List<string>(source.Platforms);
            ReleaseDate = source.ReleaseDate;
            OriginalReleaseDate = source.OriginalReleaseDate;
            OriginalGameName = source.OriginalGameName;
            IgdbVersionParentId = source.IgdbVersionParentId;
            IgdbCategory = source.IgdbCategory;
            IgdbCategoryName = source.IgdbCategoryName;
            FranchiseName = source.FranchiseName;
            IgdbFranchiseId = source.IgdbFranchiseId;
            FranchiseGames = source.FranchiseGames == null ? null : new List<FranchiseGameRef>(source.FranchiseGames);
            SteamRating = source.SteamRating;
            SteamReviewSummary = source.SteamReviewSummary;
            SteamReviewTotal = source.SteamReviewTotal;
            AgeRating = source.AgeRating;
            TimeToCompleteMainStory = source.TimeToCompleteMainStory;
            TimeToCompleteCompletionist = source.TimeToCompleteCompletionist;
            HasHeroAssetSource = source.HasHeroAssetSource;
            HasLogoAssetSource = source.HasLogoAssetSource;
            LibraryCapsuleUrl = source.LibraryCapsuleUrl;
            LibraryCapsuleCache = source.LibraryCapsuleCache;
            LibraryHeroUrl = source.LibraryHeroUrl;
            LibraryHeroCache = source.LibraryHeroCache;
            LibraryLogoUrl = source.LibraryLogoUrl;
            LibraryLogoCache = source.LibraryLogoCache;
            Media = new List<string>(source.Media);
            OfficialWebsiteUrl = source.OfficialWebsiteUrl;
            SteamPageUrl = source.SteamPageUrl;
            RawgUrl = source.RawgUrl;
            IgdbUrl = source.IgdbUrl;
            HltbUrl = source.HltbUrl;
            LaunchScript = source.LaunchScript;
            UseLaunchScriptOverride = source.UseLaunchScriptOverride;
            UseExecutableOverride = source.UseExecutableOverride;
            IsFullyImported = source.IsFullyImported;
        }

        public string FolderSizeDisplay
        {
            get
            {
                if (FolderSize <= 0) return "0 MB";
                double sizeInMb = FolderSize / (1024.0 * 1024.0);
                if (sizeInMb >= 1000)
                {
                    return $"{(sizeInMb / 1024.0):F1} GB";
                }
                return $"{sizeInMb:F0} MB";
            }
        }

        public string DateAddedDisplay => DateAdded.ToString("MMM dd, yyyy");
    }

    public sealed record FranchiseGameRef(
        int IgdbId,
        string Name,
        DateTime? ReleaseDate,
        DateTime? OriginalReleaseDate,
        string? CategoryName,
        string? CoverUrl,
        int? IgdbCategory,
        List<string>? Platforms
    );
}
