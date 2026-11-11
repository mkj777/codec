using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Codec.Models;
using Codec.Services.Fetching;
using Codec.Services.Storage;
using SteamKit2;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameIconTests : IDisposable
    {
        private const string ClientIconHash = "a4ce7b163832f35ffcbf560ca40974bccd5d4cd5";
        private const string IconHash = "858961e95fbf869f136e1770d586e0caefd4cfac";

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"codec-icons-{Guid.NewGuid():N}");
        private readonly ConcurrentQueue<string> _requests = new();

        public GameIconTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private string IconsDir => Path.Combine(_root, "Assets", "Icons");

        // ---------------------------------------------------------------- PICS

        [Fact]
        public void Pics_ReadsBothIconHashes_NextToTheArtwork()
        {
            var root = AppInfo(("clienticon", ClientIconHash), ("icon", IconHash));
            var full = new KeyValue("library_assets_full");
            var capsule = new KeyValue("library_capsule");
            var image = new KeyValue("image");
            image.Children.Add(new KeyValue("english", "capsule.jpg"));
            capsule.Children.Add(image);
            full.Children.Add(capsule);
            root["common"].Children.Add(full);

            var assets = SteamKitService.ExtractAssets(105600, root);

            Assert.NotNull(assets);
            Assert.Equal(ClientIconHash, assets!.ClientIconHash);
            Assert.Equal(IconHash, assets.IconHash);
            Assert.EndsWith("/105600/capsule.jpg", assets.CapsuleUrl);
        }

        [Fact]
        public void Pics_KeepsAnApp_ThatOnlyListsIcons()
        {
            var assets = SteamKitService.ExtractAssets(4001890, AppInfo(("clienticon", ClientIconHash)));

            Assert.NotNull(assets);
            Assert.Equal(ClientIconHash, assets!.ClientIconHash);
            Assert.Null(assets.IconHash);
            Assert.Null(assets.CapsuleUrl);
            Assert.Null(assets.HeroUrl);
            Assert.Null(assets.LogoUrl);
        }

        [Fact]
        public void Pics_StillDropsAnApp_WithNoArtworkAndNoIcons()
        {
            Assert.Null(SteamKitService.ExtractAssets(10, AppInfo(("name", "Nothing"))));
        }

        [Fact]
        public void SteamIconUrl_UsesTheCommunityHost()
        {
            Assert.Equal(
                "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/105600/abc.ico",
                SteamKitService.BuildIconUrl(105600, "abc", "ico"));
        }

        // ---------------------------------------------------------------- chain

        [Fact]
        public async Task Chain_MovesOn_WhenASourceHasNone()
        {
            var asked = new List<int>();

            var result = await GameIconService.RunChainAsync(new Func<CancellationToken, Task<GameIconService.IconResult>>[]
            {
                _ => Answer(asked, 1, GameIconService.IconResult.NotAvailable),
                _ => Answer(asked, 2, GameIconService.IconResult.Found("second.ico")),
                _ => Answer(asked, 3, GameIconService.IconResult.Found("third.ico")),
            }, Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.Equal("second.ico", result.Path);
            Assert.Equal([1, 2], asked);
        }

        [Fact]
        public async Task Chain_Stops_WhenASourceCouldNotBeAsked()
        {
            var asked = new List<int>();

            var result = await GameIconService.RunChainAsync(new Func<CancellationToken, Task<GameIconService.IconResult>>[]
            {
                _ => Answer(asked, 1, GameIconService.IconResult.Failed),
                _ => Answer(asked, 2, GameIconService.IconResult.Found("second.ico")),
            }, Ct);

            Assert.Equal(GameIconService.IconOutcome.Failed, result.Outcome);
            Assert.Null(result.Path);
            Assert.Equal([1], asked);
        }

        [Fact]
        public async Task Chain_TreatsAThrowingSourceAsFailed()
        {
            var result = await GameIconService.RunChainAsync(new Func<CancellationToken, Task<GameIconService.IconResult>>[]
            {
                _ => throw new HttpRequestException("down"),
                _ => Task.FromResult(GameIconService.IconResult.Found("never.ico")),
            }, Ct);

            Assert.Equal(GameIconService.IconOutcome.Failed, result.Outcome);
        }

        [Fact]
        public async Task Chain_EndsWithNone_WhenNoSourceHasAnIcon()
        {
            var result = await GameIconService.RunChainAsync(new Func<CancellationToken, Task<GameIconService.IconResult>>[]
            {
                _ => Task.FromResult(GameIconService.IconResult.NotAvailable),
                _ => Task.FromResult(GameIconService.IconResult.NotAvailable),
            }, Ct);

            Assert.Equal(GameIconService.IconOutcome.NotAvailable, result.Outcome);
        }

        // ---------------------------------------------------------------- Steam

        [Fact]
        public async Task Steam_PrefersTheClientIco()
        {
            var service = CreateService(Steam(ClientIconHash, IconHash), _ => Image());

            var result = await service.ResolveAsync(SteamGame(), Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.EndsWith(".ico", result.Path);
            Assert.Contains(ClientIconHash[..12], Path.GetFileName(result.Path));
            Assert.StartsWith(IconsDir, result.Path);
            Assert.Single(_requests);
            Assert.Contains("cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/105600/", _requests.Single());
        }

        [Fact]
        public async Task Steam_FallsBackToTheJpg_WhenTheIcoIsMissing()
        {
            var service = CreateService(Steam(ClientIconHash, IconHash), request => request.EndsWith(".ico") ? Status(HttpStatusCode.NotFound) : Image());

            var result = await service.ResolveAsync(SteamGame(), Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.EndsWith(".jpg", result.Path);
            Assert.Contains(IconHash[..12], Path.GetFileName(result.Path));
        }

        [Fact]
        public async Task Steam_TakesTheSteamClientsCopy_WhenTheCdnHasNone()
        {
            string steamRoot = Path.Combine(_root, "Steam");
            Directory.CreateDirectory(Path.Combine(steamRoot, "steam", "games"));
            File.WriteAllBytes(Path.Combine(steamRoot, "steam", "games", $"{ClientIconHash}.ico"), [7, 7, 7]);
            var service = CreateService(Steam(ClientIconHash, IconHash), _ => Status(HttpStatusCode.NotFound), steamRoot);

            var result = await service.ResolveAsync(SteamGame(), Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.StartsWith(IconsDir, result.Path);
            Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(result.Path!));
        }

        [Fact]
        public async Task Steam_TakesTheSteamClientsCopy_WhenOffline()
        {
            string steamRoot = Path.Combine(_root, "Steam");
            string cache = Path.Combine(steamRoot, "appcache", "librarycache", "105600");
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, $"{IconHash}.jpg"), [5, 5]);
            var service = CreateService(Steam(null, IconHash), _ => throw new HttpRequestException("offline"), steamRoot);

            var result = await service.ResolveAsync(SteamGame(), Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(result.Path!));
        }

        [Theory]
        [InlineData("503")]
        [InlineData("throw")]
        public async Task Steam_TransientFailure_EndsTheSearchWithoutAnAnswer(string answer)
        {
            var service = CreateService(Steam(ClientIconHash, IconHash), request => request.Contains("igdb")
                ? Json(IgdbIconAnswer("abc"))
                : answer == "503" ? Status(HttpStatusCode.ServiceUnavailable) : throw new HttpRequestException("down"));
            Game game = SteamGame();
            game.IgdbId = 115;

            var result = await service.ResolveAsync(game, Ct);

            Assert.Equal(GameIconService.IconOutcome.Failed, result.Outcome);
            Assert.DoesNotContain(_requests, request => request.Contains("igdb"));
            Assert.False(Directory.Exists(IconsDir) && Directory.EnumerateFiles(IconsDir).Any());
        }

        [Fact]
        public async Task Steam_LookupFailure_IsNotTakenAsNoIcon()
        {
            var steam = new FakeSteam { LookupFailed = true };
            var service = CreateService(steam, _ => Image());

            var result = await service.ResolveAsync(SteamGame(), Ct);

            Assert.Equal(GameIconService.IconOutcome.Failed, result.Outcome);
            Assert.Empty(_requests);
        }

        [Fact]
        public async Task Steam404_MovesOnToIgdb()
        {
            var service = CreateService(Steam(ClientIconHash, null), request =>
                request.Contains("steamstatic") ? Status(HttpStatusCode.NotFound)
                : request.Contains("/api/igdb/") ? Json(IgdbIconAnswer("ar57pl"))
                : Image());
            Game game = SteamGame();
            game.IgdbId = 115;

            var result = await service.ResolveAsync(game, Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.EndsWith(".png", result.Path);
            Assert.Contains(_requests, request => request == "https://images.igdb.com/igdb/image/upload/t_thumb_2x/ar57pl.png");
        }

        [Fact]
        public async Task IgdbWithoutIcon_MovesOnToSteamGridDb_AndPrefersTheOfficialOne()
        {
            var service = CreateService(new FakeSteam(), request =>
                request.Contains("/api/igdb/") ? Json("[{\"name\":\"q0\",\"result\":[]}]")
                : request.Contains("/api/griddb/icons") ? Json(GridIcons)
                : Image());
            var game = new Game { Name = "Some Game", IgdbId = 5, GridDbId = 77 };

            var result = await service.ResolveAsync(game, Ct);

            Assert.Equal(GameIconService.IconOutcome.Found, result.Outcome);
            Assert.Contains(_requests, request => request == "https://cdn2.steamgriddb.com/icon/official.ico");
            Assert.DoesNotContain(_requests, request => request.Contains("first.png"));
            Assert.Contains("griddb_77_icon_2", Path.GetFileName(result.Path));
        }

        [Fact]
        public void NeedsIcon_WhenSteamListsAHashTheFileDoesNotCarry()
        {
            string old = WriteIcon("steam_105600_clienticon_000000000000_x.ico");
            string current = WriteIcon($"steam_105600_clienticon_{ClientIconHash[..12]}_x.ico");
            var service = CreateService(Steam(ClientIconHash, IconHash), _ => Image());

            Assert.True(service.NeedsIcon(SteamGame(old)));
            Assert.False(service.NeedsIcon(SteamGame(current)));
            Assert.True(service.NeedsIcon(SteamGame(Path.Combine(_root, "gone.ico"))));
        }

        [Fact]
        public void NeedsIcon_KeepsAnExeIcon_WhileItsFileIsThere()
        {
            string exeIcon = WriteIcon("exe_abcdef012345.ico");
            var service = CreateService(new FakeSteam(), _ => Image());

            Assert.False(service.NeedsIcon(new Game { Name = "Local", LibraryIconCache = exeIcon }));
            File.Delete(exeIcon);
            Assert.True(service.NeedsIcon(new Game { Name = "Local", LibraryIconCache = exeIcon }));
        }

        // ---------------------------------------------------------------- backfill

        [Fact]
        public async Task Backfill_SkipsHiddenGames()
        {
            var steam = Steam(ClientIconHash, null);
            var service = CreateService(steam, _ => Image());
            Game visible = SteamGame();
            Game hidden = SteamGame();
            hidden.SteamID = 220;
            hidden.IsHidden = true;

            var updates = await service.BackfillAsync([visible, hidden], 2, cancellationToken: Ct);

            Assert.Equal([visible.Id], updates.Select(update => update.GameId));
            Assert.DoesNotContain(220u, steam.Asked);
            Assert.DoesNotContain(_requests, request => request.Contains("/220/"));
            Assert.Equal([105600u], steam.Prefetched);
        }

        [Fact]
        public async Task Backfill_LeavesAGameThatHasItsIcon()
        {
            string current = WriteIcon($"steam_105600_clienticon_{ClientIconHash[..12]}_x.ico");
            var service = CreateService(Steam(ClientIconHash, null), _ => Image());

            var updates = await service.BackfillAsync([SteamGame(current)], 1, cancellationToken: Ct);

            Assert.Empty(updates);
            Assert.Empty(_requests);
        }

        // ---------------------------------------------------------------- exe icons

        [Fact]
        public void IcoContainer_RoundTrips()
        {
            var frames = new[]
            {
                new IconFrame(16, 16, 0, 1, 32, Dib(16)),
                new IconFrame(48, 48, 0, 1, 32, Dib(48)),
                new IconFrame(0, 0, 0, 1, 32, Png(256)),
            };

            var parsed = IconFile.Parse(IconFile.Build(frames));

            Assert.Equal(3, parsed.Count);
            for (int i = 0; i < frames.Length; i++)
            {
                Assert.Equal(frames[i].Width, parsed[i].Width);
                Assert.Equal(frames[i].BitCount, parsed[i].BitCount);
                Assert.Equal(frames[i].Data, parsed[i].Data);
            }

            Assert.Equal([16, 48, 256], parsed.Select(frame => frame.PixelWidth));
            Assert.True(parsed[2].IsPng);
        }

        [Fact]
        public void ExeIcon_IsSkipped_WhenItsLargestFrameIsBelow32()
        {
            Assert.False(ExeIconReader.IsLargeEnough([new IconFrame(16, 16, 0, 1, 32, Dib(16)), new IconFrame(24, 24, 0, 1, 32, Dib(24))]));
            Assert.True(ExeIconReader.IsLargeEnough([new IconFrame(16, 16, 0, 1, 32, Dib(16)), new IconFrame(32, 32, 0, 1, 32, Dib(32))]));
            Assert.False(ExeIconReader.IsLargeEnough([]));
        }

        [Fact]
        public void ExeIcon_TakesEveryFrameOfTheFirstGroup()
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Assert.SkipUnless(File.Exists(exe), "explorer.exe is not where Windows keeps it");

            byte[]? icon = ExeIconReader.ReadIcon(exe);

            Assert.NotNull(icon);
            var frames = IconFile.Parse(icon!);
            Assert.True(frames.Count > 1);
            Assert.True(IconFile.LargestWidth(frames) >= 32);
            Assert.Contains(frames, frame => frame.PixelWidth <= 32);
        }

        [Fact]
        public void ExeIcon_IsNull_ForAFileWithoutIcons()
        {
            string notAnExe = Path.Combine(_root, "plain.exe");
            File.WriteAllText(notAnExe, "not a program");

            Assert.Null(ExeIconReader.ReadIcon(notAnExe));
        }

        // ---------------------------------------------------------------- SteamGridDB

        [Fact]
        public void GridIcons_PreferTheOfficialOne()
        {
            using var doc = JsonDocument.Parse(GridIcons);

            var picked = GridDbService.PickIcon(doc.RootElement.GetProperty("data"));

            Assert.Equal("https://cdn2.steamgriddb.com/icon/official.ico", picked?.Url);
            Assert.Equal(2, picked?.ImageId);
        }

        [Fact]
        public void GridIcons_TakeTheFirst_WhenNoneIsOfficial()
        {
            using var doc = JsonDocument.Parse("""{"data":[{"id":1,"style":"custom","url":"https://cdn2.steamgriddb.com/icon/first.png"},{"id":2,"style":"custom","url":"https://cdn2.steamgriddb.com/icon/second.png"}]}""");

            var picked = GridDbService.PickIcon(doc.RootElement.GetProperty("data"));

            Assert.Equal("https://cdn2.steamgriddb.com/icon/first.png", picked?.Url);
        }

        [Fact]
        public async Task GridIcons_ReportAFailedLookup_ApartFromNone()
        {
            var failing = new GridDbService(Assets(_ => Status(HttpStatusCode.BadGateway)), httpHandler: new StubHandler(_requests, _ => Status(HttpStatusCode.BadGateway)));
            var empty = new GridDbService(Assets(_ => Image()), httpHandler: new StubHandler(_requests, _ => Json("""{"success":true,"data":[]}""")));

            Assert.True((await failing.FindIconAsync(1, Ct)).LookupFailed);
            var none = await empty.FindIconAsync(1, Ct);
            Assert.False(none.LookupFailed);
            Assert.Null(none.Url);
        }

        // ---------------------------------------------------------------- model

        [Fact]
        public async Task IconPath_IsSavedAndLoadedWithTheLibrary()
        {
            string dir = Path.Combine(_root, "library");
            Directory.CreateDirectory(dir);
            var game = new Game { Name = "Terraria", LibraryIconCache = @"C:\icons\terraria.ico" };

            Assert.True(await new LibraryStorageService(dir).SaveAsync([game]));
            string json = await File.ReadAllTextAsync(Path.Combine(dir, "library.json"), Ct);
            var loaded = await new LibraryStorageService(dir).LoadAsync();

            Assert.Contains("\"LibIconCache\"", json);
            Assert.Equal(@"C:\icons\terraria.ico", loaded.Single().LibraryIconCache);
        }

        [Fact]
        public void IconPath_SurvivesSnapshotAndMerge()
        {
            var stored = new Game { Name = "Terraria", LibraryIconCache = "old.ico" };

            Assert.Equal("old.ico", stored.CreateHydrationSnapshot().LibraryIconCache);

            stored.MergeFetchedMetadataFrom(new Game { Name = "Terraria" });
            Assert.Equal("old.ico", stored.LibraryIconCache);

            stored.MergeFetchedMetadataFrom(new Game { Name = "Terraria", LibraryIconCache = "new.ico" });
            Assert.Equal("new.ico", stored.LibraryIconCache);
        }

        [Fact]
        public void Rail_FallsBackToTheCover_ThenToThePlaceholder()
        {
            string cover = WriteIcon("cover.jpg");
            string icon = WriteIcon("icon.ico");
            var game = new Game { Name = "Terraria" };

            Assert.True(game.ShowsRailIconPlaceholder);
            Assert.Null(game.RailIconCover);
            Assert.Null(game.LibraryIcon);

            game.LibraryCapsuleCache = cover;
            Assert.False(game.ShowsRailIconPlaceholder);
            Assert.Contains("cover.jpg", game.RailIconCover);

            game.LibraryIconCache = icon;
            Assert.Null(game.RailIconCover);
            Assert.Contains("icon.ico", game.LibraryIcon);
        }

        [Fact]
        public void SamePathIconRefresh_ChangesTheBoundSource()
        {
            var game = new Game { Name = "Terraria", LibraryIconCache = WriteIcon("icon.ico") };
            string? before = game.LibraryIcon;

            game.NotifyLibraryIconChanged();

            Assert.NotEqual(before, game.LibraryIcon);
            Assert.Equal(new Uri(before!).LocalPath, new Uri(game.LibraryIcon!).LocalPath);
        }

        // ---------------------------------------------------------------- helpers

        private const string GridIcons = """
            {"success":true,"data":[
              {"id":1,"style":"custom","mime":"image/png","url":"https://cdn2.steamgriddb.com/icon/first.png"},
              {"id":2,"style":"official","mime":"image/vnd.microsoft.icon","url":"https://cdn2.steamgriddb.com/icon/official.ico"}
            ]}
            """;

        private static Task<GameIconService.IconResult> Answer(List<int> asked, int index, GameIconService.IconResult result)
        {
            asked.Add(index);
            return Task.FromResult(result);
        }

        private static KeyValue AppInfo(params (string Key, string Value)[] commonValues)
        {
            var root = new KeyValue("appinfo");
            var common = new KeyValue("common");
            foreach (var (key, value) in commonValues)
            {
                common.Children.Add(new KeyValue(key, value));
            }

            root.Children.Add(common);
            return root;
        }

        private static string IgdbIconAnswer(string imageId) => $"[{{\"name\":\"q0\",\"result\":[{{\"id\":1,\"image_id\":\"{imageId}\"}}]}}]";

        private static Game SteamGame(string? iconPath = null) => new()
        {
            Name = "Terraria",
            SteamID = 105600,
            LibraryIconCache = iconPath
        };

        private static FakeSteam Steam(string? clientIcon, string? icon) => new()
        {
            Assets = new SteamLibraryAssets(105600, null, null, null, null, null, null, clientIcon, icon)
        };

        private string WriteIcon(string name)
        {
            Directory.CreateDirectory(IconsDir);
            string path = Path.Combine(IconsDir, name);
            File.WriteAllBytes(path, [1]);
            return path;
        }

        private GameAssetService Assets(Func<string, HttpResponseMessage> responder)
            => new(steamKit: null, resourceLimiter: null, httpHandler: new StubHandler(_requests, responder), assetsRootDirectory: Path.Combine(_root, "Assets"));

        private GameIconService CreateService(FakeSteam steam, Func<string, HttpResponseMessage> responder, string? steamRoot = null)
        {
            var assets = Assets(responder);
            var handler = new StubHandler(_requests, responder);
            return new GameIconService(
                assets,
                steam,
                new IgdbService(new HttpClient(handler)),
                new GridDbService(assets, httpHandler: handler),
                () => steamRoot,
                appDirectory: Path.Combine(_root, "App"),
                epicManifestsPath: Path.Combine(_root, "Manifests"));
        }

        private static byte[] Dib(int size)
        {
            var data = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 40);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), size);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), size * 2);
            return data;
        }

        private static byte[] Png(int size)
        {
            var data = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), size);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), size);
            return data;
        }

        private static HttpResponseMessage Image()
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class FakeSteam : ISteamAssetSource
        {
            public SteamLibraryAssets? Assets { get; init; }
            public bool LookupFailed { get; init; }
            public ConcurrentBag<uint> Asked { get; } = new();
            public List<uint> Prefetched { get; } = new();

            public Task<(SteamLibraryAssets? Assets, bool LookupFailed)> LookupLibraryAssetsAsync(uint appId, CancellationToken ct = default)
            {
                Asked.Add(appId);
                return Task.FromResult(LookupFailed ? ((SteamLibraryAssets?)null, true) : (Assets, false));
            }

            public Task PrefetchLibraryAssetsAsync(IEnumerable<uint> appIds, CancellationToken ct = default)
            {
                Prefetched.AddRange(appIds);
                return Task.CompletedTask;
            }

            public SteamLibraryAssets? TryGetCachedLibraryAssets(uint appId) => LookupFailed ? null : Assets;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly ConcurrentQueue<string> _requests;
            private readonly Func<string, HttpResponseMessage> _responder;

            public StubHandler(ConcurrentQueue<string> requests, Func<string, HttpResponseMessage> responder)
            {
                _requests = requests;
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = request.RequestUri!.ToString();
                _requests.Enqueue(url);
                return Task.FromResult(_responder(url));
            }
        }
    }
}
