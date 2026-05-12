using Codec.Models;
using Xunit;

namespace Codec.Tests
{
    public sealed class GameLaunchOptionsTests
    {
        [Fact]
        public void LaunchOptionsDisplay_ShowsSteam_ForSteamGameWithoutOverride()
        {
            var game = new Game
            {
                ImportedFrom = "Steam",
                SteamID = 730,
                Executable = @"C:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\cs2.exe"
            };

            Assert.Equal("Launches through Steam", game.LaunchOptionsDisplay);
            Assert.DoesNotContain("730", game.LaunchOptionsDisplay);
        }

        [Fact]
        public void LaunchOptionsDisplay_ShowsEpic_ForEpicGameWithoutOverride()
        {
            var game = new Game
            {
                ImportedFrom = "Epic Games Store",
                EpicAppId = "Fortnite",
                Executable = @"D:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"
            };

            Assert.Equal("Launches through Epic Games", game.LaunchOptionsDisplay);
        }

        [Fact]
        public void LaunchOptionsDisplay_ShowsRiot_ForRiotGameWithPlatformShortcut()
        {
            var game = new Game
            {
                ImportedFrom = "Riot Games",
                LaunchScript = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Riot Games\VALORANT.lnk"
            };

            Assert.Equal("Launches through Riot Games", game.LaunchOptionsDisplay);
            Assert.False(game.HasCustomLaunchScript);
        }

        [Fact]
        public void LaunchOptionsDisplay_ShowsExecutable_ForManualGame()
        {
            var game = new Game
            {
                ImportedFrom = "Added manually",
                Executable = @"D:\Games\Celeste\Celeste.exe"
            };

            Assert.Equal(@"D:\Games\Celeste\Celeste.exe", game.LaunchOptionsDisplay);
        }

        [Fact]
        public void LaunchOptionsDisplay_UsesLaunchScript_BeforeLauncherTarget()
        {
            var game = new Game
            {
                ImportedFrom = "Steam",
                SteamID = 620,
                Executable = @"C:\SteamLibrary\steamapps\common\Portal 2\portal2.exe",
                LaunchScript = @"D:\Launchers\portal2-custom.bat",
                UseLaunchScriptOverride = true
            };

            Assert.Equal(@"D:\Launchers\portal2-custom.bat", game.LaunchOptionsDisplay);
        }

        [Fact]
        public void LaunchOptionsDisplay_UsesExecutableOverride_BeforeLauncherTarget()
        {
            var game = new Game
            {
                ImportedFrom = "Steam",
                SteamID = 620,
                Executable = @"C:\SteamLibrary\steamapps\common\Portal 2\portal2.exe",
                UseExecutableOverride = true
            };

            Assert.Equal(@"C:\SteamLibrary\steamapps\common\Portal 2\portal2.exe", game.LaunchOptionsDisplay);
        }

        [Fact]
        public void LaunchOptionChanges_RaiseDisplayNotification()
        {
            var game = new Game();
            var changes = new List<string>();
            game.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.PropertyName))
                {
                    changes.Add(args.PropertyName);
                }
            };

            game.Executable = @"D:\Games\Hades\Hades.exe";
            game.LaunchScript = @"D:\Games\Hades\launch-hades.bat";
            game.UseLaunchScriptOverride = true;

            Assert.Contains(nameof(Game.LaunchOptionsDisplay), changes);
            Assert.Contains(nameof(Game.HasCustomLaunchScript), changes);
            Assert.Contains(nameof(Game.HasCustomLaunchOptions), changes);
        }
    }
}
