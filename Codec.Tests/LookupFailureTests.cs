using Codec.Models;
using Codec.Services.Fetching;
using Codec.Services.Resolving;
using Codec.Services.Storage;
using System.Net;
using System.Text;
using Xunit;

namespace Codec.Tests
{
    /// <summary>
    /// "The lookup failed" and "the lookup answered no" must lead to different results:
    /// only a definite answer may drop an id or a file the game already has.
    /// </summary>
    public sealed class LookupFailureTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"codec-lookup-{Guid.NewGuid():N}");

        public LookupFailureTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        public static TheoryData<string> FailedSteamAnswers => new()
        {
            "throw",
            "429",
            "503",
            "null-body",
            "garbage"
        };

        [Theory]
        [MemberData(nameof(FailedSteamAnswers))]
        public async Task SteamNameCheck_ReportsLookupFailed_WhenSteamCannotBeAsked(string answer)
        {
            var service = CreateNameService(_ => SteamAnswer(answer));

            var result = await service.TrySteamAppMatchLocalGameAsync(10, "Portal", null);

            Assert.Equal(GameNameService.SteamAppMatchOutcome.LookupFailed, result.Outcome);
            Assert.False(result.Matches);
        }

        [Fact]
        public async Task SteamNameCheck_ReportsMismatch_WhenSteamAnswersThatTheAppDoesNotExist()
        {
            var service = CreateNameService(_ => Json("{\"10\":{\"success\":false}}"));

            var result = await service.TrySteamAppMatchLocalGameAsync(10, "Portal", null);

            Assert.Equal(GameNameService.SteamAppMatchOutcome.Mismatched, result.Outcome);
        }

        [Fact]
        public async Task SteamNameCheck_ReportsMismatch_WhenSteamNamesAnotherGame()
        {
            var service = CreateNameService(_ => Json(AppDetails(10, "Half-Life")));

            var result = await service.TrySteamAppMatchLocalGameAsync(10, "Portal", null);

            Assert.Equal(GameNameService.SteamAppMatchOutcome.Mismatched, result.Outcome);
            Assert.Equal("Half-Life", result.SteamName);
        }

        [Fact]
        public async Task SteamNameCheck_ReportsMatch_WhenSteamNamesTheSameGame()
        {
            var service = CreateNameService(_ => Json(AppDetails(10, "Portal")));

            var result = await service.TrySteamAppMatchLocalGameAsync(10, "Portal", null);

            Assert.Equal(GameNameService.SteamAppMatchOutcome.Matched, result.Outcome);
            Assert.True(result.Matches);
        }

        [Fact]
        public async Task SteamNameCheck_PassesCancellationThrough_InsteadOfReportingAFailure()
        {
            var service = CreateNameService(_ => Json(AppDetails(10, "Portal")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.TrySteamAppMatchLocalGameAsync(10, "Portal", null, cts.Token));
        }

        [Fact]
        public async Task FindGameIds_FromAppIdFile_DoesNotOfferTheExeNameAsSteamName_WhenTheLookupFailed()
        {
            string exe = CreateGameWithAppIdFile(10);
            var service = CreateNameService(_ => SteamAnswer("503"));

            var (steamId, steamName) = await service.FindGameIdsAsync(exe, nameHint: "Portal");

            Assert.Equal(10, steamId);
            Assert.Null(steamName);
        }

        [Fact]
        public async Task FindGameIds_FromAppIdFile_UsesTheSteamName_WhenSteamAnswers()
        {
            string exe = CreateGameWithAppIdFile(10);
            var service = CreateNameService(_ => Json(AppDetails(10, "Portal")));

            var (steamId, steamName) = await service.FindGameIdsAsync(exe, nameHint: "Portal");

            Assert.Equal(10, steamId);
            Assert.Equal("Portal", steamName);
        }

        [Theory]
        [InlineData("throw")]
        [InlineData("503")]
        [InlineData("429")]
        public async Task ForcedCoverRefresh_KeepsTheExistingCover_WhenTheDownloadFails(string answer)
        {
            string coverPath = SeedCover(123, new byte[] { 1, 2, 3 });
            var assets = CreateAssetService(_ => SteamAnswer(answer));

            string? result = await assets.DownloadSteamLibraryCoverAsync(123, force: true);

            Assert.Equal(coverPath, result);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(coverPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(coverPath)!, "*.tmp"));
        }

        [Fact]
        public async Task ForcedCoverRefresh_ReplacesTheCover_WhenTheDownloadSucceeds()
        {
            string coverPath = SeedCover(123, new byte[] { 1, 2, 3 });
            var assets = CreateAssetService(_ => Bytes(new byte[] { 9, 9 }));

            string? result = await assets.DownloadSteamLibraryCoverAsync(123, force: true);

            Assert.Equal(coverPath, result);
            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(coverPath));
        }

        [Fact]
        public async Task ForcedCoverRefresh_DoesNotDeleteTheFile_WhenSteamAnswersThatTheImageIsGone()
        {
            string coverPath = SeedCover(123, new byte[] { 1, 2, 3 });
            var assets = CreateAssetService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

            await assets.DownloadSteamLibraryCoverAsync(123, force: true);

            Assert.True(File.Exists(coverPath));
        }

        [Fact]
        public async Task ImageCache_ReportsFailedNotMissing_WhenTheSourceIsUnreachable()
        {
            var assets = CreateAssetService(_ => SteamAnswer("503"));

            var result = await assets.CacheImageWithOutcomeAsync("Logos", "k", "https://cdn.example/logo.png");

            Assert.Equal(GameAssetService.AssetFetchOutcome.Failed, result.Outcome);
        }

        [Fact]
        public async Task ImageCache_ReportsMissing_WhenTheSourceAnswers404()
        {
            var assets = CreateAssetService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

            var result = await assets.CacheImageWithOutcomeAsync("Logos", "k", "https://cdn.example/logo.png");

            Assert.Equal(GameAssetService.AssetFetchOutcome.NotAvailable, result.Outcome);
        }

        [Fact]
        public async Task DisplayedAssets_KeepLogoSourceAndHero_WhenTheirDownloadsFail()
        {
            string cover = WriteFile("cover.jpg");
            string hero = WriteFile("hero.jpg");
            var game = SteamGameWithArtwork(cover, hero);
            var service = CreateDisplayedAssetService(_ => SteamAnswer("503"));

            var result = await service.EnsureDisplayedAssetsAsync(game, force: true);

            Assert.True(result.HasLogoSource);
            Assert.Equal(hero, result.HeroCachePath);
            Assert.Equal(cover, result.CapsuleCachePath);
        }

        [Fact]
        public async Task DisplayedAssets_DropLogoSource_WhenTheLogoIsDefinitelyMissing()
        {
            string cover = WriteFile("cover.jpg");
            string hero = WriteFile("hero.jpg");
            var game = SteamGameWithArtwork(cover, hero);
            var service = CreateDisplayedAssetService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

            var result = await service.EnsureDisplayedAssetsAsync(game, force: true);

            Assert.False(result.HasLogoSource);
            Assert.Equal(cover, result.CapsuleCachePath);
        }

        private static Game SteamGameWithArtwork(string cover, string hero) => new()
        {
            Name = "Portal",
            SteamID = 400,
            LibraryCapsuleCache = cover,
            LibraryHeroUrl = "https://cdn.example/hero.jpg",
            LibraryHeroCache = hero,
            HasHeroAssetSource = true,
            LibraryLogoUrl = "https://cdn.example/logo.png",
            HasLogoAssetSource = true
        };

        private DisplayedAssetService CreateDisplayedAssetService(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var assets = CreateAssetService(responder);
            return new DisplayedAssetService(assets, new GridDbService(assets), new RawgDetailsService(new MetadataCache()));
        }

        private GameAssetService CreateAssetService(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            new(steamKit: null, resourceLimiter: null, httpHandler: new StubHandler(responder), assetsRootDirectory: Path.Combine(_root, "Assets"));

        private static GameNameService CreateNameService(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            new(new GameDetailsService(new MetadataCache()), httpHandler: new StubHandler(responder));

        private string SeedCover(int steamId, byte[] content)
        {
            string dir = Path.Combine(_root, "Assets", "Capsules");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"steam_{steamId}_library_600x900.jpg");
            File.WriteAllBytes(path, content);
            return path;
        }

        private string WriteFile(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, "asset");
            return path;
        }

        private string CreateGameWithAppIdFile(int steamId)
        {
            string dir = Path.Combine(_root, "Game");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "Game-Win64-Shipping.exe");
            File.WriteAllText(exe, string.Empty);
            File.WriteAllText(Path.Combine(dir, "steam_appid.txt"), steamId.ToString());
            return exe;
        }

        private static string AppDetails(int steamId, string name) =>
            $"{{\"{steamId}\":{{\"success\":true,\"data\":{{\"name\":\"{name}\",\"release_date\":{{\"date\":\"9 Oct, 2007\"}}}}}}}}";

        private static HttpResponseMessage SteamAnswer(string answer) => answer switch
        {
            "throw" => throw new HttpRequestException("network down"),
            "429" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "null-body" => Json("null"),
            "garbage" => Json("<html>busy</html>"),
            _ => throw new ArgumentOutOfRangeException(nameof(answer))
        };

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] body)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_responder(request));
            }
        }
    }
}
