using Codec.Services.Resolving;
using Codec.Services.Storage;
using System.Reflection;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameNameServiceTests
    {
        [Theory]
        [InlineData("resident evil 2", "resident evil 4")]
        [InlineData("resident evil ii", "resident evil 4")]
        [InlineData("re2", "resident evil 4")]
        [InlineData("sim city", "sim city 4")]
        public void CalculateMatchScore_RejectsSeriesNumberMismatches(string localName, string steamName)
        {
            float score = CalculateMatchScore(localName, steamName);

            Assert.Equal(0f, score);
        }

        [Fact]
        public void CalculateMatchScore_UsesExpectedSeriesNumbersFromNameHint()
        {
            float wrongSeriesScore = CalculateMatchScore(
                "resident evil",
                "resident evil 4",
                expectedSeriesNumbers: new HashSet<int> { 2 });

            float matchingSeriesScore = CalculateMatchScore(
                "resident evil",
                "resident evil 2",
                expectedSeriesNumbers: new HashSet<int> { 2 });

            Assert.Equal(0f, wrongSeriesScore);
            Assert.True(matchingSeriesScore > 0f);
        }

        [Theory]
        [InlineData("Resident Evil 2", "Resident Evil 4", false)]
        [InlineData("Resident Evil 2", "Resident Evil 2", true)]
        [InlineData("SimCity", "SimCity 4 Deluxe Edition", false)]
        [InlineData("Need for SpeedTM Rivals", "Need for Speed Rivals", true)]
        [InlineData("Resident Evil HD Remaster", "Resident Evil / biohazard HD REMASTER", true)]
        [InlineData("Resident Evil HD Remaster", "Resident Evil", true)]
        [InlineData("BioShock Remastered", "BioShock", true)]
        [InlineData("Resident Evil 0", "Resident Evil 0 / biohazard 0 HD REMASTER", true)]
        [InlineData("BioShock", "BioShock / BioShock Infinite Bundle", false)]
        [InlineData("Mortal Kombat 11 Aftermath Edition", "Mortal Kombat 11", false)]
        [InlineData("CloverPit-SteamGG.NET", "CloverPit", true)]
        [InlineData("LoveChoice-Steamrip.com", "LoveChoice", true)]
        public void SteamNameMatchesLocalName_RejectsWrongSteamAppTitles(string localName, string steamName, bool expected)
        {
            var service = new GameNameService(new GameDetailsService(new MetadataCache()));

            bool matches = service.SteamNameMatchesLocalName(localName, steamName);

            Assert.Equal(expected, matches);
        }

        [Fact]
        public void CalculateMatchScore_NeedForSpeedRivalsScoresHighestAmongReturnedSteamResults()
        {
            var steamResults = new (uint AppId, string Name)[]
            {
                (1222680, "Need for Speed™ Heat "),
                (1846380, "Need for Speed™ Unbound"),
                (1262580, "Need for Speed™ Payback"),
                (1262540, "Need for Speed™"),
                (1262560, "Need for Speed™ Most Wanted"),
                (1328660, "Need for Speed™ Hot Pursuit Remastered"),
                (1262600, "Need for Speed™ Rivals"),
            };

            string localName = NormalizeName("Need for SpeedTM Rivals");
            var best = steamResults
                .Select(result => new
                {
                    result.AppId,
                    Score = CalculateMatchScore(localName, NormalizeName(result.Name))
                })
                .OrderByDescending(result => result.Score)
                .First();

            Assert.Equal(1262600u, best.AppId);
            Assert.Equal(1.0f, best.Score);
        }

        [Fact]
        public void SelectBestSteamMatch_UsesNewestReleaseYearForExactTie()
        {
            var local = new GameNameService.LocalGameCandidate
            {
                DetectedName = "Resident Evil 2",
                FullPath = @"C:\Games\Resident Evil 2\re2.exe",
                ExecutableName = "re2",
                MetadataSource = "folder",
                ExpectedSeriesNumbers = new HashSet<int> { 2 }
            };

            var older = new GameNameService.GameMatch
            {
                SteamAppId = 999999,
                SteamName = "Resident Evil 2",
                LocalPath = local.FullPath,
                ConfidenceScore = 1.0f,
                MatchedSearchTerm = "resident evil 2",
                LocalData = local,
                Method = GameNameService.MatchMethod.FuzzySearch,
                SteamReleaseYear = 1998
            };

            var newer = older with
            {
                SteamAppId = 883710,
                SteamReleaseYear = 2019
            };

            var best = SelectBestSteamMatch(new[] { older, newer });

            Assert.NotNull(best);
            Assert.Equal(883710u, best!.SteamAppId);
        }

        [Fact]
        public void TryGetExeCopyrightInfo_DropsZlibAuthorFromBinaryScan()
        {
            string path = Path.Combine(Path.GetTempPath(), $"codec-zlib-{Guid.NewGuid():N}.exe");
            File.WriteAllText(path, "garbage prefix Copyright 1995-2023 Jean-loup Gailly and Mark Adler garbage suffix");

            try
            {
                var service = new GameNameService(new GameDetailsService(new MetadataCache()));
                var info = service.TryGetExeCopyrightInfo(path);

                Assert.Equal("none", info.Source);
                Assert.Empty(info.Years);
                Assert.Null(info.Text);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TryGetExeCopyrightInfo_KeepsLegitimateBinaryScanResult()
        {
            string path = Path.Combine(Path.GetTempPath(), $"codec-real-{Guid.NewGuid():N}.exe");
            File.WriteAllText(path, "binary blob Copyright (C) 2021 Santa Monica Studio more bytes");

            try
            {
                var service = new GameNameService(new GameDetailsService(new MetadataCache()));
                var info = service.TryGetExeCopyrightInfo(path);

                Assert.Equal("binary-string-scan", info.Source);
                Assert.Contains(2021, info.Years);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TryGetExeCopyrightInfo_DropsInnoSetupCopyrightFromBinaryScan()
        {
            string path = Path.Combine(Path.GetTempPath(), $"codec-inno-{Guid.NewGuid():N}.exe");
            File.WriteAllText(path, "binary blob Copyright (C) 1997-2021 Jordan Russell more bytes");

            try
            {
                var service = new GameNameService(new GameDetailsService(new MetadataCache()));
                var info = service.TryGetExeCopyrightInfo(path);

                Assert.Equal("none", info.Source);
                Assert.Empty(info.Years);
                Assert.Null(info.Text);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TryGetExeCopyrightInfo_DropsUnityCopyrightFromBinaryScan()
        {
            string path = Path.Combine(Path.GetTempPath(), $"codec-unity-{Guid.NewGuid():N}.exe");
            File.WriteAllText(path, "binary blob Copyright (c) 2005-2025 Unity Technologies. All rights reserved. more bytes");

            try
            {
                var service = new GameNameService(new GameDetailsService(new MetadataCache()));
                var info = service.TryGetExeCopyrightInfo(path);

                Assert.Equal("none", info.Source);
                Assert.Empty(info.Years);
                Assert.Null(info.Text);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void BuildCopyrightInfo_DropsUnityVersionResourceCopyright()
        {
            var method = typeof(GameNameService).GetMethod(
                "BuildCopyrightInfo",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            var info = (GameNameService.ExeCopyrightInfo)method.Invoke(
                null,
                new object[] { "(c) 2005-2025 Unity Technologies. All rights reserved.", "version-resource" })!;

            Assert.Equal("none", info.Source);
            Assert.Empty(info.Years);
            Assert.Null(info.Text);
        }

        [Fact]
        public void ExtractCopyrightYears_ReturnsAllReasonableYears()
        {
            var years = GameNameService.ExtractCopyrightYears("Copyright (c) 2003-2013 Electronic Arts");

            Assert.Contains(2003, years);
            Assert.Contains(2013, years);
        }

        [Fact]
        public void ReleaseYearMatchesCopyrightYears_RequiresExactYearWhenCopyrightExists()
        {
            var years = new HashSet<int> { 2013 };

            Assert.True(GameNameService.ReleaseYearMatchesCopyrightYears(years, 2013));
            Assert.False(GameNameService.ReleaseYearMatchesCopyrightYears(years, 2010));
            Assert.False(GameNameService.ReleaseYearMatchesCopyrightYears(years, null));
            Assert.True(GameNameService.ReleaseYearMatchesCopyrightYears(new HashSet<int>(), 2010));
        }

        private static float CalculateMatchScore(
            string localName,
            string steamName,
            IReadOnlySet<int>? expectedSeriesNumbers = null)
        {
            var service = new GameNameService(new GameDetailsService(new MetadataCache()));
            var candidate = new GameNameService.LocalGameCandidate
            {
                DetectedName = localName,
                FullPath = @"C:\Games\Test\game.exe",
                ExecutableName = "game",
                MetadataSource = "file_metadata",
                ExpectedSeriesNumbers = expectedSeriesNumbers ?? new HashSet<int>()
            };

            var method = typeof(GameNameService).GetMethod(
                "CalculateMatchScore",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            return (float)method.Invoke(service, new object[] { localName, steamName, candidate })!;
        }

        private static GameNameService.GameMatch? SelectBestSteamMatch(IEnumerable<GameNameService.GameMatch> matches)
        {
            var method = typeof(GameNameService).GetMethod(
                "SelectBestSteamMatch",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            return (GameNameService.GameMatch?)method.Invoke(null, new object[] { matches });
        }

        private static string NormalizeName(string name)
        {
            var service = new GameNameService(new GameDetailsService(new MetadataCache()));
            var method = typeof(GameNameService).GetMethod(
                "NormalizeName",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            return (string)method.Invoke(service, new object[] { name })!;
        }
    }
}
