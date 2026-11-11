using System;
using System.Collections.Generic;

namespace Codec.Models
{
    public partial class Game
    {
        /// <summary>
        /// Takes what a fresh import of this game found, without letting a provider that came
        /// back empty blank what is already stored. A fetched value replaces the stored one only
        /// when it is actually there; timestamps keep the newer of the two.
        /// </summary>
        /// <remarks>
        /// User-owned state is deliberately left alone: the id, the date added, the favorite,
        /// the launch script and both override switches, and any flag the player sets on the
        /// game. The import never carries them, so a copy of them is only ever stale.
        /// </remarks>
        public void MergeFetchedMetadataFrom(Game source)
        {
            if (source == null || ReferenceEquals(source, this))
            {
                return;
            }

            // What the sync saw on disk and in the account, not fetched metadata: it always lands.
            IsInstalled = source.IsInstalled;
            IsSteamOwned = source.IsSteamOwned;
            SteamAppType = MergeText(source.SteamAppType, SteamAppType);
            ImportedFrom = MergeText(source.ImportedFrom, ImportedFrom)!;
            FolderLocation = MergeText(source.FolderLocation, FolderLocation)!;
            if (!UseExecutableOverride)
            {
                Executable = MergeText(source.Executable, Executable)!;
            }

            if (source.FolderSize > 0)
            {
                FolderSize = source.FolderSize;
            }

            LastPlayedUtc = MergeNewer(source.LastPlayedUtc, LastPlayedUtc);
            if (source.SteamAchievementsLastCheckedUtc.HasValue &&
                (!SteamAchievementsLastCheckedUtc.HasValue || source.SteamAchievementsLastCheckedUtc >= SteamAchievementsLastCheckedUtc))
            {
                SteamAchievementsLastCheckedUtc = source.SteamAchievementsLastCheckedUtc;
                if (source.SteamAchievementsTotal is > 0 && source.SteamAchievementsUnlocked.HasValue)
                {
                    SteamAchievementsUnlocked = source.SteamAchievementsUnlocked;
                    SteamAchievementsTotal = source.SteamAchievementsTotal;
                }
            }

            // external ids
            SteamID = source.SteamID ?? SteamID;
            SteamMetadataAppId = source.SteamMetadataAppId ?? SteamMetadataAppId;
            EpicAppId = MergeText(source.EpicAppId, EpicAppId);
            RawgID = source.RawgID ?? RawgID;
            RawgSlug = MergeText(source.RawgSlug, RawgSlug);
            IgdbId = source.IgdbId ?? IgdbId;
            GridDbId = source.GridDbId ?? GridDbId;

            // details
            Name = MergeText(source.Name, Name)!;
            MetadataLookupName = MergeText(source.MetadataLookupName, MetadataLookupName);
            Publisher = MergeText(source.Publisher, Publisher);
            Developer = MergeText(source.Developer, Developer);

            // An empty taxonomy list is an answer ("checked, none"); only null means not asked.
            // Treating empty as absent would leave the game unchecked and re-queried forever.
            if (source.Genres != null) Genres = new List<string>(source.Genres);
            if (source.Themes != null) Themes = new List<string>(source.Themes);
            if (source.GameModes != null) GameModes = new List<string>(source.GameModes);
            Categories = MergeList(source.Categories, Categories);
            Platforms = MergeList(source.Platforms, Platforms);

            // Price and discount come from one response, so a present price brings its discount
            // along, including a discount that has ended.
            if (IsMergeValuePresent(source.Price))
            {
                Price = source.Price;
                PriceDiscount = source.PriceDiscount;
            }

            Description = MergeText(source.Description, Description);
            ReleaseDate = source.ReleaseDate ?? ReleaseDate;
            OriginalReleaseDate = source.OriginalReleaseDate ?? OriginalReleaseDate;
            OriginalGameName = MergeText(source.OriginalGameName, OriginalGameName);
            IgdbVersionParentId = source.IgdbVersionParentId ?? IgdbVersionParentId;
            IgdbCategory = source.IgdbCategory ?? IgdbCategory;
            IgdbCategoryName = MergeText(source.IgdbCategoryName, IgdbCategoryName);
            FranchiseName = MergeText(source.FranchiseName, FranchiseName);
            IgdbFranchiseId = source.IgdbFranchiseId ?? IgdbFranchiseId;
            if (source.FranchiseGames is { Count: > 0 })
            {
                FranchiseGames = new List<FranchiseGameRef>(source.FranchiseGames);
            }

            SteamRating = source.SteamRating ?? SteamRating;
            SteamReviewSummary = MergeText(source.SteamReviewSummary, SteamReviewSummary);
            SteamReviewTotal = source.SteamReviewTotal ?? SteamReviewTotal;
            AgeRating = MergeText(source.AgeRating, AgeRating);
            TimeToCompleteMainStory = source.TimeToCompleteMainStory ?? TimeToCompleteMainStory;
            TimeToCompleteCompletionist = source.TimeToCompleteCompletionist ?? TimeToCompleteCompletionist;

            // The recommendation is read from the same store page as the support level.
            if (source.ControllerSupport != ControllerSupportLevel.Unknown)
            {
                ControllerSupport = source.ControllerSupport;
                IsControllerRecommended = source.IsControllerRecommended;
            }

            // artwork
            HasHeroAssetSource = HasHeroAssetSource || source.HasHeroAssetSource;
            HasLogoAssetSource = HasLogoAssetSource || source.HasLogoAssetSource;
            LibraryCapsuleUrl = MergeText(source.LibraryCapsuleUrl, LibraryCapsuleUrl);
            LibraryCapsuleCache = MergeText(source.LibraryCapsuleCache, LibraryCapsuleCache);
            LibraryHeroUrl = MergeText(source.LibraryHeroUrl, LibraryHeroUrl);
            LibraryHeroCache = MergeText(source.LibraryHeroCache, LibraryHeroCache);
            LibraryLogoUrl = MergeText(source.LibraryLogoUrl, LibraryLogoUrl);
            LibraryLogoCache = MergeText(source.LibraryLogoCache, LibraryLogoCache);
            LibraryIconCache = MergeText(source.LibraryIconCache, LibraryIconCache);
            if (source.Media is { Count: > 0 })
            {
                Media = new List<string>(source.Media);
            }

            // links
            OfficialWebsiteUrl = MergeText(source.OfficialWebsiteUrl, OfficialWebsiteUrl);
            SteamPageUrl = MergeText(source.SteamPageUrl, SteamPageUrl);
            RawgUrl = MergeText(source.RawgUrl, RawgUrl);
            IgdbUrl = MergeText(source.IgdbUrl, IgdbUrl);
            HltbUrl = MergeText(source.HltbUrl, HltbUrl);

            // A finished import never becomes unfinished because a later one fell short.
            IsFullyImported = IsFullyImported || source.IsFullyImported;
        }

        private static bool IsMergeValuePresent(string? value) => !string.IsNullOrWhiteSpace(value);

        private static string? MergeText(string? incoming, string? current) => IsMergeValuePresent(incoming) ? incoming : current;

        private static List<string>? MergeList(List<string>? incoming, List<string>? current)
            => incoming is { Count: > 0 } ? new List<string>(incoming) : current;

        private static DateTime? MergeNewer(DateTime? incoming, DateTime? current)
            => !current.HasValue || incoming > current ? incoming ?? current : current;
    }
}
