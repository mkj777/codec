using Codec.Models;
using Codec.Services.Fetching;
using System.Net;
using System.Net.Http;
using Xunit;

namespace Codec.Tests
{
    public sealed class IgdbServiceTests
    {
        [Fact]
        public async Task PopulateFromIgdbAsync_ForVersionRecord_StoresOriginalReleaseMetadata()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                return JsonResponse($$"""
                [{
                  "id": 100,
                  "name": "Doom Remastered",
                  "first_release_date": {{UnixDate(2025, 8, 1)}},
                  "game_type": { "type": "remaster" },
                  "version_parent": {
                    "id": 10,
                    "name": "Doom",
                    "first_release_date": {{UnixDate(1994, 1, 1)}}
                  }
                }]
                """);
            });

            var game = new Game { IgdbId = 100 };
            await service.PopulateFromIgdbAsync(game);

            Assert.Equal(new DateTime(2025, 8, 1), game.ReleaseDate?.Date);
            Assert.Equal(new DateTime(1994, 1, 1), game.OriginalReleaseDate?.Date);
            Assert.Equal("Doom", game.OriginalGameName);
            Assert.Equal(10, game.IgdbVersionParentId);
            Assert.Equal(9, game.IgdbCategory);
            Assert.Equal("Remaster", game.IgdbCategoryName);
            Assert.True(game.IsRemakeOrRemaster);
            Assert.True(game.HasOriginalReleaseDate);
            Assert.Equal("Originally released: 1994 (Doom)", game.OriginalReleaseDisplay);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_ForMainGame_ClearsOriginalReleaseMetadata()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                return JsonResponse($$"""
                [{
                  "id": 20,
                  "name": "Doom",
                  "first_release_date": {{UnixDate(1994, 1, 1)}},
                  "game_type": { "type": "main_game" }
                }]
                """);
            });

            var game = new Game
            {
                IgdbId = 20,
                OriginalGameName = "Old Parent",
                OriginalReleaseDate = new DateTime(1980, 1, 1),
                IgdbVersionParentId = 1,
                IgdbCategory = 9
            };

            await service.PopulateFromIgdbAsync(game);

            Assert.Null(game.OriginalReleaseDate);
            Assert.Null(game.OriginalGameName);
            Assert.Null(game.IgdbVersionParentId);
            Assert.Equal(0, game.IgdbCategory);
            Assert.Equal("Main Game", game.IgdbCategoryName);
            Assert.False(game.IsRemakeOrRemaster);
            Assert.False(game.HasOriginalReleaseDate);
            Assert.Equal(string.Empty, game.OriginalReleaseDisplay);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_ForPortWithoutParent_ClearsOriginalReleaseMetadata()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                return JsonResponse($$"""
                [{
                  "id": 30,
                  "name": "Mystery Port",
                  "first_release_date": {{UnixDate(2020, 1, 1)}},
                  "game_type": { "type": "port" }
                }]
                """);
            });

            var game = new Game
            {
                IgdbId = 30,
                OriginalGameName = "Old Parent",
                OriginalReleaseDate = new DateTime(1980, 1, 1),
                IgdbVersionParentId = 1,
                IgdbCategory = 9
            };

            await service.PopulateFromIgdbAsync(game);

            Assert.Null(game.OriginalReleaseDate);
            Assert.Null(game.OriginalGameName);
            Assert.Null(game.IgdbVersionParentId);
            Assert.Equal(11, game.IgdbCategory);
            Assert.Equal("Port", game.IgdbCategoryName);
            Assert.False(game.IsRemakeOrRemaster);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_WhenVersionParentAbsent_FallsBackToNameSearch()
        {
            // game_type=remaster but no version_parent — triggers name-strip fallback
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                // Main game fetch (id = 40, "Doom Remastered", no version_parent)
                if (body.Contains("where id = 40;", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""
                    [{
                      "id": 40,
                      "name": "Doom Remastered",
                      "first_release_date": {{UnixDate(2025, 8, 1)}},
                      "game_type": { "type": "remaster" }
                    }]
                    """);
                }

                // Name fallback: search for "Doom"
                if (body.Contains("name = \"Doom\"", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""
                    [{
                      "id": 10,
                      "name": "Doom",
                      "first_release_date": {{UnixDate(1994, 1, 1)}}
                    }]
                    """);
                }

                return JsonResponse("[]");
            });

            var game = new Game { IgdbId = 40 };
            await service.PopulateFromIgdbAsync(game);

            Assert.Equal("Doom", game.OriginalGameName);
            Assert.Equal(new DateTime(1994, 1, 1), game.OriginalReleaseDate?.Date);
            Assert.Equal(9, game.IgdbCategory);
            Assert.True(game.IsRemakeOrRemaster);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_UsesParentGameFieldWhenVersionParentAbsent()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                // Main game fetch (id = 50, remaster, parent_game = 10, no version_parent)
                if (body.Contains("where id = 50;", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""
                    [{
                      "id": 50,
                      "name": "BioShock Remastered",
                      "first_release_date": {{UnixDate(2016, 9, 13)}},
                      "game_type": { "type": "remaster" },
                      "parent_game": 10
                    }]
                    """);
                }

                // Fetch parent by id=10
                if (body.Contains("where id = 10;", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""
                    [{
                      "id": 10,
                      "name": "BioShock",
                      "first_release_date": {{UnixDate(2007, 8, 21)}}
                    }]
                    """);
                }

                return JsonResponse("[]");
            });

            var game = new Game { IgdbId = 50 };
            await service.PopulateFromIgdbAsync(game);

            Assert.Equal("BioShock", game.OriginalGameName);
            Assert.Equal(new DateTime(2007, 8, 21), game.OriginalReleaseDate?.Date);
            Assert.Equal(10, game.IgdbVersionParentId);
            Assert.True(game.IsRemakeOrRemaster);
        }

        [Fact]
        public void HydrationSnapshot_CopiesOriginalReleaseMetadata()
        {
            var source = new Game
            {
                OriginalReleaseDate = new DateTime(1994, 1, 1),
                OriginalGameName = "Doom",
                IgdbVersionParentId = 10,
                IgdbCategory = 9,
                IgdbCategoryName = "Remaster",
                FranchiseName = "Doom",
                IgdbFranchiseId = 42
            };

            var snapshot = source.CreateHydrationSnapshot();
            var target = new Game();
            target.ApplyHydrationSnapshot(snapshot);

            Assert.Equal(source.OriginalReleaseDate, snapshot.OriginalReleaseDate);
            Assert.Equal(source.OriginalGameName, snapshot.OriginalGameName);
            Assert.Equal(source.IgdbVersionParentId, snapshot.IgdbVersionParentId);
            Assert.Equal(source.IgdbCategory, snapshot.IgdbCategory);
            Assert.Equal(source.IgdbCategoryName, snapshot.IgdbCategoryName);
            Assert.Equal(source.IsRemakeOrRemaster, snapshot.IsRemakeOrRemaster);
            Assert.Equal(source.FranchiseName, snapshot.FranchiseName);
            Assert.Equal(source.IgdbFranchiseId, snapshot.IgdbFranchiseId);
            Assert.Equal(source.OriginalReleaseDate, target.OriginalReleaseDate);
            Assert.Equal(source.OriginalGameName, target.OriginalGameName);
            Assert.Equal(source.IgdbVersionParentId, target.IgdbVersionParentId);
            Assert.Equal(source.IgdbCategory, target.IgdbCategory);
            Assert.Equal(source.IgdbCategoryName, target.IgdbCategoryName);
            Assert.Equal(source.IsRemakeOrRemaster, target.IsRemakeOrRemaster);
            Assert.Equal(source.FranchiseName, target.FranchiseName);
            Assert.Equal(source.IgdbFranchiseId, target.IgdbFranchiseId);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_ExtractsFranchise()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                return JsonResponse($$"""
                [{
                  "id": 200,
                  "name": "Half-Life 2",
                  "first_release_date": {{UnixDate(2004, 11, 16)}},
                  "game_type": { "type": "main_game" },
                  "franchises": [{ "id": 42, "name": "Half-Life" }]
                }]
                """);
            });

            var game = new Game { IgdbId = 200 };
            await service.PopulateFromIgdbAsync(game);

            Assert.Equal("Half-Life", game.FranchiseName);
            Assert.Equal(42, game.IgdbFranchiseId);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_DlcDoesNotSetIsRemakeOrRemaster()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                return JsonResponse($$"""
                [{
                  "id": 300,
                  "name": "Some DLC",
                  "first_release_date": {{UnixDate(2020, 6, 1)}},
                  "game_type": { "type": "dlc_addon" },
                  "version_parent": { "id": 50, "name": "Some Game", "first_release_date": {{UnixDate(2018, 1, 1)}} }
                }]
                """);
            });

            var game = new Game { IgdbId = 300 };
            await service.PopulateFromIgdbAsync(game);

            Assert.False(game.IsRemakeOrRemaster);
            Assert.Null(game.OriginalReleaseDate);
            Assert.Null(game.OriginalGameName);
        }

        [Fact]
        public async Task PopulateFromIgdbAsync_StoresFranchiseGames()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/game_time_to_beats", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse("[]");

                if (uri.Contains("/franchises", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                    [{ "id": 5, "name": "BioShock", "games": [273, 409710] }]
                    """);
                }

                if (body.Contains("where id = (", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""
                    [
                      {
                        "id": 273,
                        "name": "BioShock",
                        "first_release_date": {{UnixDate(2007, 8, 21)}},
                        "game_type": { "type": "main_game" }
                      },
                      {
                        "id": 409710,
                        "name": "BioShock Remastered",
                        "first_release_date": {{UnixDate(2016, 9, 13)}},
                        "game_type": { "type": "remaster" },
                        "version_parent": { "first_release_date": {{UnixDate(2007, 8, 21)}} }
                      }
                    ]
                    """);
                }

                // Main game fetch for IgdbId=273
                return JsonResponse($$"""
                [{
                  "id": 273,
                  "name": "BioShock",
                  "first_release_date": {{UnixDate(2007, 8, 21)}},
                  "game_type": { "type": "main_game" },
                  "franchises": [{ "id": 5, "name": "BioShock" }]
                }]
                """);
            });

            var game = new Game { IgdbId = 273 };
            await service.PopulateFromIgdbAsync(game);

            Assert.NotNull(game.FranchiseGames);
            Assert.Equal(2, game.FranchiseGames!.Count);

            var remastered = game.FranchiseGames.First(g => g.Name == "BioShock Remastered");
            Assert.Equal(new DateTime(2007, 8, 21), remastered.OriginalReleaseDate?.Date);
            Assert.Equal("Remaster", remastered.CategoryName);
        }

        [Fact]
        public async Task FetchFranchiseTimelineAsync_ReturnsSortedEntries()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/games", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse($$"""
                    [
                      {
                        "id": 1,
                        "name": "BioShock",
                        "first_release_date": {{UnixDate(2007, 8, 21)}},
                        "game_type": { "type": "main_game" }
                      },
                      {
                        "id": 2,
                        "name": "BioShock Remastered",
                        "first_release_date": {{UnixDate(2016, 9, 13)}},
                        "game_type": { "type": "remaster" },
                        "version_parent": {
                          "id": 1,
                          "name": "BioShock",
                          "first_release_date": {{UnixDate(2007, 8, 21)}}
                        }
                      }
                    ]
                    """);
                }
                return JsonResponse("[]");
            });

            var entries = await service.FetchFranchiseTimelineAsync(franchiseId: 99);

            Assert.Equal(2, entries.Count);
            Assert.Equal("BioShock", entries[0].Name);
            Assert.Null(entries[0].OriginalReleaseDate);
            Assert.Equal("BioShock Remastered", entries[1].Name);
            Assert.Equal(new DateTime(2007, 8, 21), entries[1].OriginalReleaseDate?.Date);
            Assert.Equal("BioShock", entries[1].OriginalGameName);
            Assert.Equal(9, entries[1].IgdbCategory);
        }

        private static IgdbService CreateService(Func<string, string, HttpResponseMessage> responder)
        {
            var handler = new DelegateHandler(responder);
            return new IgdbService(new HttpClient(handler));
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) };

        private static long UnixDate(int year, int month, int day) =>
            new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        private sealed class DelegateHandler : HttpMessageHandler
        {
            private readonly Func<string, string, HttpResponseMessage> _responder;

            public DelegateHandler(Func<string, string, HttpResponseMessage> responder)
                => _responder = responder;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return _responder(request.RequestUri?.ToString() ?? string.Empty, body);
            }
        }
    }
}
