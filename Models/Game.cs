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
        [SetsRequiredMembers]
        public Game()
        {
            // Initialize “required” strings to non-null defaults
            name = string.Empty;
            executable = string.Empty;
            folderLocation = string.Empty;
            importedFrom = string.Empty;
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
            importedFrom.StartsWith("Steam", StringComparison.OrdinalIgnoreCase) ? "Steam" : importedFrom;

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
        [ObservableProperty] private List<string>? platforms;

        [ObservableProperty] private DateTime? releaseDate;
        [ObservableProperty] private double? steamRating;
        [ObservableProperty] private string? steamReviewSummary;
        [ObservableProperty] private int? steamReviewTotal;
        [ObservableProperty] private string? ageRating;
        [ObservableProperty] private int? timeToCompleteMainStory;
        [ObservableProperty] private int? timeToCompleteCompletionist;

        public IEnumerable<string> PlatformLogoUris => (Platforms ?? Enumerable.Empty<string>())
            .Select(MapPlatformToLogoUri)
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Select(uri => uri!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // game assets with cache for offline first, effective path resolution
        private static string GetEffectiveAssetPath(string? cachePath, string? url, string placeholder)
        {
            if (!string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath))
            {
                return cachePath;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return placeholder;
        }
        // placeholders
        private const string PlaceholderCapsule = "https://placehold.co/600x900/1c1c1c/ffffff?text=Capsule";
        private const string PlaceholderHero = "https://placehold.co/1920x620/1c1c1c/ffffff?text=Hero";
        private const string PlaceholderLogo = "https://placehold.co/1280x260/1c1c1c/ffffff?text=Logo";

        // capsule
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibCapsule))] private string? libCapsuleUrl;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibCapsule))] private string? libCapsuleCache;
        public string LibCapsule => GetEffectiveAssetPath(libCapsuleCache, libCapsuleUrl, PlaceholderCapsule);

        // hero
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibHero))] private string? libHeroUrl;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibHero))] private string? libHeroCache;
        public string LibHero => GetEffectiveAssetPath(libHeroCache, libHeroUrl, PlaceholderHero);

        // logo
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibLogo))] private string? libLogoUrl;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(LibLogo))] private string? libLogoCache;
        public string LibLogo => GetEffectiveAssetPath(libLogoCache, libLogoUrl, PlaceholderLogo);

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

        // Helper method to map platform names to logo URIs
        private static string? MapPlatformToLogoUri(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                return null;
            }

            var normalized = platform.Trim().ToLowerInvariant();

            if (normalized.Contains("playstation")) return "ms-appx:///Assets/playstation_logo.png";
            if (normalized.Contains("xbox")) return "ms-appx:///Assets/xbox_logo.png";
            if (normalized.Contains("nintendo") || normalized.Contains("switch")) return "ms-appx:///Assets/NintendoSwitch_logo.png";
            if (normalized.Contains("ios")) return "ms-appx:///Assets/iOS_logo.png";
            if (normalized.Contains("android")) return "ms-appx:///Assets/android_logo.png";
            if (normalized.Contains("mac")) return "ms-appx:///Assets/MacOS_logo.png";
            if (normalized.Contains("linux")) return "ms-appx:///Assets/linux_logo.png";
            if (normalized.Contains("pc") || normalized.Contains("windows")) return "ms-appx:///Assets/windows_logo.png";

            return null;
        }
    }
}