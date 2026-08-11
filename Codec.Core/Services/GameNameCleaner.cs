using System;
using System.Text.RegularExpressions;

namespace Codec.Services
{
    internal static class GameNameCleaner
    {
        private static readonly Regex TrailingDomainTagRegex = new(
            @"(?:^|[\s._-]+)[A-Za-z0-9_]+\.(?:com|net|ru)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex FriendPassSuffixRegex = new(
            @"(?:^|[\s._:-]+)friend(?:['\u2019]?s)?\s+pass\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static string RemoveTrailingDomainTag(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string cleaned = name.Trim();
            string previous;
            do
            {
                previous = cleaned;
                cleaned = TrailingDomainTagRegex.Replace(cleaned, string.Empty);
                cleaned = cleaned.Trim().Trim(' ', '.', '-', '_');
            }
            while (!string.Equals(cleaned, previous, StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(cleaned));

            return string.IsNullOrWhiteSpace(cleaned)
                ? name.Trim()
                : cleaned;
        }

        public static bool TryGetFriendPassBaseName(string? name, out string baseName)
        {
            baseName = string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string cleaned = RemoveTrailingDomainTag(name);
            string stripped = FriendPassSuffixRegex.Replace(cleaned, string.Empty)
                .Trim()
                .Trim(' ', '.', '-', '_', ':');

            if (string.IsNullOrWhiteSpace(stripped) ||
                string.Equals(stripped, cleaned, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            baseName = stripped;
            return true;
        }

        public static string GetMetadataLookupName(string? displayName, string? explicitLookupName = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitLookupName))
            {
                return RemoveTrailingDomainTag(explicitLookupName);
            }

            if (TryGetFriendPassBaseName(displayName, out string friendPassBaseName))
            {
                return friendPassBaseName;
            }

            return RemoveTrailingDomainTag(displayName);
        }
    }
}
