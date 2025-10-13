namespace Codec
{
    public class Game
    {
        // the display name of the game
        public string Name { get; set; }

        // the full path to the executable
        public string Executable { get; set; }

        // steamID of the game
        public int? SteamID { get; set; }

        // rawg.io ID of the game
        public int? RawgID { get; set; }

        // path to the Cover, can be a URL or local path
        public string? Cover { get; set; }

        // optional: Path to .bat- or .ps1 Script to launch the game
        public string? LaunchScript { get; set; }
    }
}