using Codec.Models;
using Codec.Services.Fetching;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Codec.Tests
{
    public sealed class IgdbServiceTests
    {
        [Fact]
        public async Task FindSteamIdByIgdbIdAsync_UsesExternalGamesUid()
        {
            string? capturedUri = null;
            string? capturedBody = null;
            var service = CreateService((uri, body) =>
            {
                capturedUri = uri;
                capturedBody = body;
                return JsonResponse("""
                [{
                  "id": 2035022,
                  "game": 2074,
                  "name": "Need for Speed Rivals",
                  "uid": "1262600"
                }]
                """);
            });

            int? steamId = await service.FindSteamIdByIgdbIdAsync(2074);

            Assert.Equal(1262600, steamId);
            Assert.Contains("/external_games", capturedUri);
            Assert.Contains("fields game, name, uid;", capturedBody);
            Assert.Contains("where game = 2074 & external_game_source = 1;", capturedBody);
        }

        [Fact]
        public async Task FindSteamIdByIgdbIdAsync_ReturnsNullWhenNoSteamExternalGameExists()
        {
            var service = CreateService((_, _) => JsonResponse("[]"));

            int? steamId = await service.FindSteamIdByIgdbIdAsync(2074);

            Assert.Null(steamId);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_SkipsReleaseYearMismatches()
        {
            string? capturedBody = null;
            var service = CreateService((_, body) =>
            {
                capturedBody = body;
                return JsonResponse($$"""
                [
                  {
                    "id": 24780,
                    "name": "SimCity 4 Deluxe Edition",
                    "first_release_date": {{UnixDate(2010, 7, 20)}}
                  },
                  {
                    "id": 1234,
                    "name": "SimCity",
                    "first_release_date": {{UnixDate(2013, 3, 5)}}
                  }
                ]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("SimCity", new HashSet<int> { 2013 });

            Assert.Equal(1234, match.Id);
            Assert.Equal(2013, match.ReleaseYear);
            Assert.Contains("limit 10;", capturedBody);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_PrefersNewestSteamBackedExactName()
        {
            string? capturedExternalBody = null;
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/external_games", StringComparison.OrdinalIgnoreCase))
                {
                    capturedExternalBody = body;
                    return JsonResponse("""
                    [
                      { "game": 10, "name": "Resident Evil 2", "uid": "999999" },
                      { "game": 20, "name": "Resident Evil 2", "uid": "883710" }
                    ]
                    """);
                }

                return JsonResponse($$"""
                [
                  {
                    "id": 10,
                    "name": "Resident Evil 2",
                    "first_release_date": {{UnixDate(1998, 1, 21)}}
                  },
                  {
                    "id": 20,
                    "name": "Resident Evil 2",
                    "first_release_date": {{UnixDate(2019, 1, 25)}}
                  },
                  {
                    "id": 30,
                    "name": "Resident Evil 2: Extra DLC",
                    "first_release_date": {{UnixDate(2020, 1, 1)}}
                  }
                ]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("Resident Evil 2", allowedReleaseYears: null);

            Assert.Equal(20, match.Id);
            Assert.Equal(2019, match.ReleaseYear);
            Assert.Contains("where game = (10, 20, 30) & external_game_source = 1;", capturedExternalBody);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_NormalizesTrademarkAndCachesSingleSteamId()
        {
            string? capturedGamesBody = null;
            string? capturedExternalBody = null;
            int externalCalls = 0;
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/external_games", StringComparison.OrdinalIgnoreCase))
                {
                    externalCalls++;
                    capturedExternalBody = body;
                    return JsonResponse("""
                    [{
                      "id": 2035022,
                      "game": 2074,
                      "name": "Need for Speed Rivals",
                      "uid": "1262600"
                    }]
                    """);
                }

                capturedGamesBody = body;
                return JsonResponse($$"""
                [{
                  "id": 2074,
                  "name": "Need for Speed Rivals",
                  "first_release_date": {{UnixDate(2013, 11, 15)}}
                }]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("Need for SpeedTM Rivals", allowedReleaseYears: null);
            int? steamId = await service.FindSteamIdByIgdbIdAsync(2074);

            Assert.Equal(2074, match.Id);
            Assert.Equal(2013, match.ReleaseYear);
            Assert.Equal(1262600, steamId);
            Assert.Equal(1, externalCalls);
            Assert.NotNull(capturedGamesBody);
            Assert.Contains("search \"Need for Speed Rivals\";", capturedGamesBody!);
            Assert.DoesNotContain("SpeedTM", capturedGamesBody!);
            Assert.Contains("where game = 2074 & external_game_source = 1;", capturedExternalBody);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_PrefersRemakeOverRemasterWithDisambiguatedSteamName()
        {
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/external_games", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                    [
                      { "game": 19686, "name": "RESIDENT EVIL 2 / BIOHAZARD RE:2", "uid": "883710" },
                      { "game": 396728, "name": "Resident Evil 2 (1998)", "uid": "4249110" }
                    ]
                    """);
                }

                return JsonResponse($$"""
                [
                  {
                    "id": 19686,
                    "name": "Resident Evil 2",
                    "first_release_date": {{UnixDate(2019, 1, 25)}},
                    "game_type": { "type": "remake" }
                  },
                  {
                    "id": 396728,
                    "name": "Resident Evil 2",
                    "first_release_date": {{UnixDate(2024, 8, 27)}},
                    "game_type": { "type": "remaster" }
                  }
                ]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("Resident Evil 2", allowedReleaseYears: null);

            Assert.Equal(19686, match.Id);
            Assert.Equal(2019, match.ReleaseYear);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_RetriesWithStrippedSuffixWhenPrimaryMatchIsWeak()
        {
            int gamesCalls = 0;
            string? secondGamesBody = null;
            var service = CreateService((uri, body) =>
            {
                if (uri.Contains("/external_games", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""
                    [
                      { "game": 8254, "name": "Resident Evil / biohazard HD REMASTER", "uid": "304240" }
                    ]
                    """);
                }

                gamesCalls++;
                if (gamesCalls == 1)
                {
                    return JsonResponse($$"""
                    [{
                      "id": 41858,
                      "name": "Resident Evil 6 Remastered",
                      "first_release_date": {{UnixDate(2016, 3, 29)}},
                      "game_type": { "type": "remaster" }
                    }]
                    """);
                }

                secondGamesBody = body;
                return JsonResponse($$"""
                [
                  {
                    "id": 424,
                    "name": "Resident Evil",
                    "first_release_date": {{UnixDate(1996, 3, 22)}},
                    "game_type": { "type": "main_game" }
                  },
                  {
                    "id": 8254,
                    "name": "Resident Evil",
                    "first_release_date": {{UnixDate(2014, 11, 27)}},
                    "game_type": { "type": "remaster" }
                  }
                ]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("Resident Evil HD Remaster", allowedReleaseYears: null);

            Assert.Equal(8254, match.Id);
            Assert.Equal(2014, match.ReleaseYear);
            Assert.Equal(2, gamesCalls);
            Assert.NotNull(secondGamesBody);
            Assert.Contains("search \"Resident Evil\";", secondGamesBody!);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_AcceptsCopyrightYearMatchAgainstPlatformReleaseDate()
        {
            var service = CreateService((uri, _) =>
            {
                if (uri.Contains("/external_games", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("[]");
                }

                return JsonResponse($$"""
                [{
                  "id": 1942,
                  "name": "God of War",
                  "first_release_date": {{UnixDate(2018, 4, 20)}},
                  "release_dates": [
                    { "date": {{UnixDate(2018, 4, 20)}} },
                    { "date": {{UnixDate(2022, 1, 14)}} }
                  ],
                  "game_type": { "type": "main_game" }
                }]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("God of War", new HashSet<int> { 2021 });

            Assert.Equal(1942, match.Id);
            Assert.Equal(2018, match.ReleaseYear);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_AllowsOneYearCopyrightTolerance()
        {
            var service = CreateService((uri, _) =>
            {
                if (uri.Contains("/external_games", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("[]");
                }

                return JsonResponse($$"""
                [{
                  "id": 1234,
                  "name": "SimCity",
                  "first_release_date": {{UnixDate(2012, 12, 31)}}
                }]
                """);
            });

            var match = await service.FindIgdbMatchByNameAsync("SimCity", new HashSet<int> { 2013 });

            Assert.Equal(1234, match.Id);
            Assert.Equal(2012, match.ReleaseYear);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_ReturnsNullWhenCopyrightYearCannotMatch()
        {
            var service = CreateService((_, _) => JsonResponse($$"""
            [{
              "id": 24780,
              "name": "SimCity 4 Deluxe Edition",
              "first_release_date": {{UnixDate(2010, 7, 20)}}
            }]
            """));

            var match = await service.FindIgdbMatchByNameAsync("SimCity", new HashSet<int> { 2013 });

            Assert.Null(match.Id);
            Assert.Null(match.ReleaseYear);
        }

        [Fact]
        public async Task FindIgdbMatchByNameAsync_ReturnsNullWhenCopyrightYearExistsButReleaseDateMissing()
        {
            var service = CreateService((_, _) => JsonResponse("""
            [{
              "id": 999,
              "name": "SimCity"
            }]
            """));

            var match = await service.FindIgdbMatchByNameAsync("SimCity", new HashSet<int> { 2013 });

            Assert.Null(match.Id);
            Assert.Null(match.ReleaseYear);
        }

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
            Assert.Equal("Originally released: 1994", game.OriginalReleaseDisplay);
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
                IgdbId = 2074,
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

            Assert.Equal(source.IgdbId, snapshot.IgdbId);
            Assert.Equal(source.OriginalReleaseDate, snapshot.OriginalReleaseDate);
            Assert.Equal(source.OriginalGameName, snapshot.OriginalGameName);
            Assert.Equal(source.IgdbVersionParentId, snapshot.IgdbVersionParentId);
            Assert.Equal(source.IgdbCategory, snapshot.IgdbCategory);
            Assert.Equal(source.IgdbCategoryName, snapshot.IgdbCategoryName);
            Assert.Equal(source.IsRemakeOrRemaster, snapshot.IsRemakeOrRemaster);
            Assert.Equal(source.FranchiseName, snapshot.FranchiseName);
            Assert.Equal(source.IgdbFranchiseId, snapshot.IgdbFranchiseId);
            Assert.Equal(source.IgdbId, target.IgdbId);
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
        public void GameJsonSerialization_PreservesIgdbId()
        {
            var game = new Game
            {
                Name = "Need for Speed Rivals",
                Executable = @"E:\Games\Need for SpeedTM Rivals\NFS14.exe",
                FolderLocation = @"E:\Games\Need for SpeedTM Rivals",
                ImportedFrom = "Heuristic Scan",
                SteamID = 1262600,
                IgdbId = 2074
            };

            string json = JsonSerializer.Serialize(game);
            var roundTrip = JsonSerializer.Deserialize<Game>(json);

            Assert.Contains("\"IgdbId\":2074", json);
            Assert.NotNull(roundTrip);
            Assert.Equal(2074, roundTrip!.IgdbId);
            Assert.Equal(1262600, roundTrip.SteamID);
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
                  "collections": [{ "id": 42, "name": "Half-Life" }]
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

                if (uri.Contains("/collections", StringComparison.OrdinalIgnoreCase))
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
                  "collections": [{ "id": 5, "name": "BioShock" }]
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
