using System.Text.Json;
using Codec.Models;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameHiddenStateTests
    {
        // Matches LibraryStorageService, which is what reads and writes library.json.
        private static readonly JsonSerializerOptions LibraryJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        [Fact]
        public void NewGame_IsNotHidden()
        {
            Assert.False(new Game().IsHidden);
        }

        [Fact]
        public void HiddenFlag_SurvivesJsonRoundTrip()
        {
            var game = new Game { Name = "Cheat Engine", IsHidden = true };

            string json = JsonSerializer.Serialize(game, LibraryJsonOptions);
            Game? loaded = JsonSerializer.Deserialize<Game>(json, LibraryJsonOptions);

            Assert.Contains("\"IsHidden\": true", json);
            Assert.NotNull(loaded);
            Assert.True(loaded!.IsHidden);
            Assert.Equal("Cheat Engine", loaded.Name);
        }

        [Fact]
        public void LegacyEntryWithoutTheKey_LoadsAsNotHidden()
        {
            const string legacy = """
                {
                  "Id": "6f1c2a8e-0b3d-4c55-9a51-2f7d3e1b9c40",
                  "Name": "Portal 2",
                  "ImportedFrom": "Steam",
                  "SteamID": 620,
                  "IsFavorite": true
                }
                """;

            Game? loaded = JsonSerializer.Deserialize<Game>(legacy, LibraryJsonOptions);

            Assert.NotNull(loaded);
            Assert.False(loaded!.IsHidden);
            Assert.True(loaded.IsFavorite);
        }

        [Fact]
        public void HiddenFlag_SurvivesHydrationSnapshot()
        {
            var game = new Game { Name = "Steamworks Common Redistributables", IsHidden = true };

            Game snapshot = game.CreateHydrationSnapshot();
            var target = new Game();
            target.ApplyHydrationSnapshot(snapshot);

            Assert.True(snapshot.IsHidden);
            Assert.True(target.IsHidden);
        }
    }
}
