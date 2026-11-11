using System.Globalization;
using Codec.Models;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameAssetProbeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"codec-probe-{Guid.NewGuid():N}");

        public GameAssetProbeTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }

        [Fact]
        public void CoverThatAppearsAfterDownload_CountsWithoutANotification()
        {
            string coverPath = Path.Combine(_root, "cover.jpg");
            var game = new Game { LibraryCapsuleCache = coverPath };

            Assert.False(game.IsLibraryCoverReady);
            Assert.DoesNotContain("cover.jpg", game.LibraryCapsule);

            File.WriteAllText(coverPath, "asset");

            Assert.True(game.IsLibraryCoverReady);
            Assert.Contains("cover.jpg", game.LibraryCapsule);
        }

        [Fact]
        public void SamePathRefresh_ChangesTheBoundSource_ButNotTheFile()
        {
            string coverPath = CreateAsset("cover.jpg");
            var game = new Game { LibraryCapsuleCache = coverPath };
            string before = game.LibraryCapsule;

            game.NotifyLibraryCapsuleChanged();
            string after = game.LibraryCapsule;

            Assert.NotEqual(before, after);
            Assert.Equal(new Uri(before).LocalPath, new Uri(after).LocalPath);
            Assert.Equal(coverPath, new Uri(after).LocalPath);

            game.NotifyLibraryCapsuleChanged();
            Assert.NotEqual(after, game.LibraryCapsule);
        }

        [Fact]
        public void DisplayedAssets_FlipToReady_WhenTheLastFileLands()
        {
            string coverPath = CreateAsset("cover.jpg");
            string heroPath = Path.Combine(_root, "hero.jpg");
            var game = new Game
            {
                LibraryCapsuleCache = coverPath,
                LibraryHeroCache = heroPath,
                HasHeroAssetSource = true
            };

            Assert.False(game.DisplayedAssetsReady);

            File.WriteAllText(heroPath, "asset");

            Assert.True(game.DisplayedAssetsReady);
        }

        [Fact]
        public void FoundCover_IsTrustedBriefly_ThenCheckedAgain()
        {
            string coverPath = CreateAsset("cover.jpg");
            var game = new Game { LibraryCapsuleCache = coverPath };

            Assert.True(game.IsLibraryCoverReady);

            File.Delete(coverPath);

            Assert.True(game.IsLibraryCoverReady);

            Thread.Sleep(TimeSpan.FromMilliseconds(Game.AssetProbeLifetimeMs + 150));

            Assert.False(game.IsLibraryCoverReady);
        }

        [Fact]
        public void SamePathRefresh_ForgetsWhatWasKnown()
        {
            string coverPath = CreateAsset("cover.jpg");
            var game = new Game { LibraryCapsuleCache = coverPath };

            Assert.True(game.IsLibraryCoverReady);
            Assert.Contains("cover.jpg", game.LibraryCapsule);

            File.Delete(coverPath);
            game.NotifyLibraryCapsuleChanged();

            Assert.False(game.IsLibraryCoverReady);
            Assert.DoesNotContain("cover.jpg", game.LibraryCapsule);
        }

        [Fact]
        public void PathChange_IsNeverAnsweredFromTheOldPath()
        {
            string first = CreateAsset("first.jpg");
            string second = Path.Combine(_root, "second.jpg");
            var game = new Game { LibraryCapsuleCache = first };

            Assert.True(game.IsLibraryCoverReady);

            game.LibraryCapsuleCache = second;

            Assert.False(game.IsLibraryCoverReady);
            Assert.DoesNotContain("first.jpg", game.LibraryCapsule);
        }

        [Fact]
        public void ReadinessReads_DoNotChangeWhichNotificationsFire()
        {
            string coverPath = CreateAsset("cover.jpg");
            var game = new Game();
            var changes = new List<string>();
            game.PropertyChanged += (_, args) => changes.Add(args.PropertyName ?? string.Empty);

            game.LibraryCapsuleCache = coverPath;
            _ = game.IsLibraryCoverReady;
            _ = game.LibraryCapsule;
            _ = game.DisplayedAssetsReady;
            game.NotifyLibraryCapsuleChanged();

            Assert.Equal(
                new[]
                {
                    nameof(Game.LibraryCapsuleCache),
                    nameof(Game.LibraryCapsule),
                    nameof(Game.DisplayedAssetsReady),
                    nameof(Game.IsLibraryCoverReady),
                    nameof(Game.RailIconCover),
                    nameof(Game.ShowsRailIconPlaceholder),
                    nameof(Game.LibraryCapsule),
                    nameof(Game.IsLibraryCoverReady),
                    nameof(Game.RailIconCover),
                    nameof(Game.ShowsRailIconPlaceholder)
                },
                changes);
        }

        [Fact]
        public void DateAddedDisplay_IsEnglish_WhateverTheMachineCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var game = new Game { DateAdded = new DateTime(2026, 10, 3) };

                Assert.Equal("Oct 03, 2026", game.DateAddedDisplay);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private string CreateAsset(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, "asset");
            return path;
        }
    }
}
