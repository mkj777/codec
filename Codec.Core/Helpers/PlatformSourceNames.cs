using System;

namespace Codec.Helpers
{
    public static class PlatformSourceNames
    {
        public const string Steam = "Steam";
        public const string EpicGames = "Epic Games";
        public const string LegacyEpicGamesStore = "Epic Games Store";
        public const string RiotGames = "Riot Games";
        public const string HeuristicScan = "Heuristic Scan";

        public static string NormalizeImportSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            string trimmed = source.Trim();
            return string.Equals(trimmed, LegacyEpicGamesStore, StringComparison.OrdinalIgnoreCase)
                ? EpicGames
                : trimmed;
        }

        public static bool IsEpicGames(string? source) =>
            !string.IsNullOrWhiteSpace(source) &&
            NormalizeImportSource(source).StartsWith(EpicGames, StringComparison.OrdinalIgnoreCase);
    }
}
