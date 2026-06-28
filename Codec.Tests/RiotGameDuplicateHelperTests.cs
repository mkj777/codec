using Codec.Helpers;
using Codec.Models;
using Xunit;

namespace Codec.Tests
{
    public sealed class RiotGameDuplicateHelperTests
    {
        [Fact]
        public void DeduplicateByIdentity_RemovesRiotGamesWithSameFolder()
        {
            var olderDuplicate = new Game
            {
                Name = "Legends of Runeterra",
                FolderLocation = @"C:\Riot Games\LoR",
                ImportedFrom = "Riot Games",
                IsFullyImported = true,
                RawgID = 383515
            };
            var betterDuplicate = new Game
            {
                Name = "Legends of Runeterra",
                FolderLocation = @"C:\Riot Games\LoR\",
                ImportedFrom = "Riot Games",
                IsFullyImported = true,
                RawgID = 383515,
                IgdbId = 124701
            };
            var nonRiotSameFolder = new Game
            {
                Name = "Manual LoR",
                FolderLocation = @"C:\Riot Games\LoR",
                ImportedFrom = "Added manually"
            };

            var deduplicated = RiotGameDuplicateHelper.DeduplicateByIdentity(new[]
            {
                olderDuplicate,
                betterDuplicate,
                nonRiotSameFolder
            });

            Assert.Equal(2, deduplicated.Count);
            Assert.Contains(betterDuplicate, deduplicated);
            Assert.Contains(nonRiotSameFolder, deduplicated);
        }

        [Fact]
        public void DeduplicateByIdentity_RemovesRiotGamesWithSameLaunchTarget()
        {
            var firstLocation = new Game
            {
                Name = "Legends of Runeterra",
                FolderLocation = @"C:\Riot Games\LoR",
                ImportedFrom = "Riot Games",
                LaunchScript = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Legends of Runeterra.lnk",
                IsFullyImported = true,
                RawgID = 383515
            };
            var secondLocation = new Game
            {
                Name = "Legends of Runeterra",
                FolderLocation = @"D:\Riot Games\LoR",
                ImportedFrom = "Riot Games",
                LaunchScript = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Legends of Runeterra.lnk",
                IsFullyImported = true,
                RawgID = 383515,
                IgdbId = 124701
            };

            var deduplicated = RiotGameDuplicateHelper.DeduplicateByIdentity(new[]
            {
                firstLocation,
                secondLocation
            });

            var keptGame = Assert.Single(deduplicated);
            Assert.Same(secondLocation, keptGame);
        }

        [Fact]
        public void DeduplicateByIdentity_KeepsRiotGameWithMoreMetadataOverArtworkOnlyEntry()
        {
            string coverPath = CreateTempAsset();
            string heroPath = CreateTempAsset();

            try
            {
                var artworkOnly = new Game
                {
                    Name = "Legends of Runeterra",
                    FolderLocation = @"C:\Riot Games\LoR",
                    ImportedFrom = "Riot Games",
                    LaunchScript = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Legends of Runeterra.lnk",
                    IsFullyImported = true,
                    RawgID = 383515,
                    LibraryCapsuleCache = coverPath,
                    LibraryHeroCache = heroPath,
                    HasLogoAssetSource = false
                };
                var enriched = new Game
                {
                    Name = "Legends of Runeterra",
                    FolderLocation = @"D:\Riot Games\LoR",
                    ImportedFrom = "Riot Games",
                    LaunchScript = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Legends of Runeterra.lnk",
                    IsFullyImported = true,
                    RawgID = 383515,
                    IgdbId = 124701,
                    Description = "A strategy card game set in the League of Legends universe.",
                    Publisher = "Riot Games",
                    Developer = "Riot Games",
                    Platforms = new List<string> { "PC (Microsoft Windows)", "Android", "iOS" },
                    Genres = new List<string> { "Strategy", "Card & Board Game" },
                    ReleaseDate = new DateTime(2020, 4, 30)
                };

                var deduplicated = RiotGameDuplicateHelper.DeduplicateByIdentity(new[]
                {
                    artworkOnly,
                    enriched
                });

                var keptGame = Assert.Single(deduplicated);
                Assert.Same(enriched, keptGame);
            }
            finally
            {
                DeleteIfExists(coverPath);
                DeleteIfExists(heroPath);
            }
        }

        [Fact]
        public void IsDuplicateGame_MatchesExistingRiotFolder()
        {
            var existing = new Game
            {
                Name = "VALORANT",
                FolderLocation = @"C:\Riot Games\VALORANT\",
                ImportedFrom = "Riot Games"
            };

            Assert.True(RiotGameDuplicateHelper.IsDuplicateGame(
                "Riot Games",
                @"C:\Riot Games\VALORANT",
                launchScript: null,
                new[] { existing }));
        }

        [Fact]
        public void IsDuplicateGame_MatchesExistingRiotLaunchTarget()
        {
            var existing = new Game
            {
                Name = "Legends of Runeterra",
                FolderLocation = @"C:\Riot Games\LoR",
                ImportedFrom = "Riot Games",
                LaunchScript = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Legends of Runeterra.lnk"
            };

            Assert.True(RiotGameDuplicateHelper.IsDuplicateGame(
                "Riot Games",
                @"D:\Riot Games\LoR",
                @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\Legends of Runeterra.lnk",
                new[] { existing }));
        }

        private static string CreateTempAsset()
        {
            string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
            File.WriteAllText(path, "asset");
            return path;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // best effort
            }
        }
    }
}
