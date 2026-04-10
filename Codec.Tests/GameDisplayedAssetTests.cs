using Codec.Models;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameDisplayedAssetTests
    {
        [Fact]
        public void HeroCacheChange_RaisesLibHeroNotification()
        {
            var game = new Game();
            var changes = new List<string>();
            string heroPath = CreateTempAsset(".jpg");

            try
            {
                game.PropertyChanged += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.PropertyName))
                    {
                        changes.Add(args.PropertyName);
                    }
                };

                game.LibHeroCache = heroPath;

                Assert.Contains(nameof(Game.LibHero), changes);
                Assert.Contains(nameof(Game.DisplayedAssetsReady), changes);
            }
            finally
            {
                DeleteIfExists(heroPath);
            }
        }

        [Fact]
        public void LogoCacheChange_RaisesLibLogoNotification()
        {
            var game = new Game();
            var changes = new List<string>();
            string logoPath = CreateTempAsset(".png");

            try
            {
                game.PropertyChanged += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.PropertyName))
                    {
                        changes.Add(args.PropertyName);
                    }
                };

                game.HasLogoAssetSource = true;
                game.LibLogoCache = logoPath;

                Assert.Contains(nameof(Game.LibLogo), changes);
                Assert.Contains(nameof(Game.DisplayedAssetsReady), changes);
            }
            finally
            {
                DeleteIfExists(logoPath);
            }
        }

        [Fact]
        public void DisplayedAssetsReady_IsTrue_WhenCoverAndHeroAreCached_AndNoLogoSourceExists()
        {
            string coverPath = CreateTempAsset(".jpg");
            string heroPath = CreateTempAsset(".jpg");

            try
            {
                var game = new Game
                {
                    LibCapsuleCache = coverPath,
                    LibHeroCache = heroPath,
                    HasHeroAssetSource = true,
                    HasLogoAssetSource = false
                };

                Assert.True(game.DisplayedAssetsReady);
            }
            finally
            {
                DeleteIfExists(coverPath);
                DeleteIfExists(heroPath);
            }
        }

        [Fact]
        public void DisplayedAssetsReady_IsFalse_WhenRequiredCachedFileIsMissing()
        {
            string missingCoverPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
            string heroPath = CreateTempAsset(".jpg");

            try
            {
                var game = new Game
                {
                    LibCapsuleCache = missingCoverPath,
                    LibHeroCache = heroPath,
                    HasHeroAssetSource = true
                };

                Assert.False(game.DisplayedAssetsReady);
            }
            finally
            {
                DeleteIfExists(heroPath);
            }
        }

        private static string CreateTempAsset(string extension)
        {
            string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
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
