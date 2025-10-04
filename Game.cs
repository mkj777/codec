namespace Codec
{
    public class Game
    {
        // The display name of the game
        public string Name { get; set; }

        // The full path to the executable
        public string Executable { get; set; }

        // Path to the Cover, can be a URL or local path
        public string Cover { get; set; }

        // Optional: Path to .bat- or .ps1 Script to launch the game
        public string? LaunchScript { get; set; }
    }
}