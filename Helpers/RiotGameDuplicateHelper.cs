using Codec.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Codec.Helpers
{
    public static class RiotGameDuplicateHelper
    {
        public static bool IsRiotSource(string? source) =>
            string.Equals(PlatformSourceNames.NormalizeImportSource(source), PlatformSourceNames.RiotGames, StringComparison.OrdinalIgnoreCase);

        public static bool IsDuplicateGame(string? source, string? folderLocation, string? launchScript, IEnumerable<Game> games)
        {
            if (!IsRiotSource(source))
            {
                return false;
            }

            bool hasFolderKey = TryGetPathKey(folderLocation, out var folderKey);
            bool hasLaunchTargetKey = TryGetPathKey(launchScript, out var launchTargetKey);

            return games.Any(game =>
                IsRiotSource(game.ImportedFrom) &&
                MatchesAnyIdentity(
                    folderKey,
                    hasFolderKey,
                    launchTargetKey,
                    hasLaunchTargetKey,
                    game));
        }

        public static List<Game> DeduplicateByIdentity(IEnumerable<Game> games)
        {
            var result = new List<Game>();
            var riotIdentityIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var game in games)
            {
                if (!IsRiotSource(game.ImportedFrom))
                {
                    result.Add(game);
                    continue;
                }

                var identityKeys = GetIdentityKeys(game).ToList();
                if (identityKeys.Count == 0)
                {
                    result.Add(game);
                    continue;
                }

                int existingIndex = identityKeys
                    .Select(key => riotIdentityIndexes.TryGetValue(key, out int index) ? index : -1)
                    .FirstOrDefault(index => index >= 0, -1);

                if (existingIndex < 0)
                {
                    int newIndex = result.Count;
                    foreach (var key in identityKeys)
                    {
                        riotIdentityIndexes[key] = newIndex;
                    }
                    result.Add(game);
                    continue;
                }

                if (GetQualityScore(game) > GetQualityScore(result[existingIndex]))
                {
                    result[existingIndex] = game;
                }

                foreach (var key in identityKeys)
                {
                    riotIdentityIndexes[key] = existingIndex;
                }
            }

            return result;
        }

        public static bool IsDuplicateFolder(string? source, string? folderLocation, IEnumerable<Game> games) =>
            IsDuplicateGame(source, folderLocation, launchScript: null, games);

        public static List<Game> DeduplicateByFolder(IEnumerable<Game> games) =>
            DeduplicateByIdentity(games);

        public static bool TryGetPathKey(string? path, out string pathKey)
        {
            pathKey = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                pathKey = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                pathKey = path.Trim()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return !string.IsNullOrWhiteSpace(pathKey);
        }

        public static bool TryGetFolderKey(string? folderLocation, out string folderKey) =>
            TryGetPathKey(folderLocation, out folderKey);

        private static IEnumerable<string> GetIdentityKeys(Game game)
        {
            if (TryGetPathKey(game.FolderLocation, out var folderKey))
            {
                yield return $"folder:{folderKey}";
            }

            if (TryGetPathKey(game.LaunchScript, out var launchTargetKey))
            {
                yield return $"launch:{launchTargetKey}";
            }
        }

        private static bool MatchesAnyIdentity(
            string folderKey,
            bool hasFolderKey,
            string launchTargetKey,
            bool hasLaunchTargetKey,
            Game game)
        {
            return hasFolderKey &&
                   TryGetPathKey(game.FolderLocation, out var existingFolderKey) &&
                   string.Equals(existingFolderKey, folderKey, StringComparison.OrdinalIgnoreCase)
                || hasLaunchTargetKey &&
                   TryGetPathKey(game.LaunchScript, out var existingLaunchTargetKey) &&
                   string.Equals(existingLaunchTargetKey, launchTargetKey, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetQualityScore(Game game)
        {
            int score = 0;

            if (game.IgdbId.HasValue)
                score += 1_000_000;
            if (!string.IsNullOrWhiteSpace(game.Description))
                score += 500_000;
            if (game.Platforms?.Count > 0)
                score += 250_000;
            if (game.Genres?.Count > 0)
                score += 250_000;
            if (game.ReleaseDate.HasValue)
                score += 200_000;
            if (!string.IsNullOrWhiteSpace(game.Publisher))
                score += 100_000;
            if (!string.IsNullOrWhiteSpace(game.Developer))
                score += 100_000;
            if (game.Media?.Count > 0)
                score += 75_000;
            if (game.RawgID.HasValue)
                score += 50_000;
            if (game.GridDbId.HasValue)
                score += 25_000;
            if (game.DisplayedAssetsReady)
                score += 10_000;
            if (game.IsFullyImported)
                score += 5_000;

            return score;
        }
    }
}
