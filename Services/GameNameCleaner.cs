using System;
using System.Text.RegularExpressions;

namespace Codec.Services
{
    internal static class GameNameCleaner
    {
        private static readonly Regex TrailingDomainTagRegex = new(
            @"(?:^|[\s._-]+)[A-Za-z0-9_]+\.(?:com|net|ru)\s*$",
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
    }
}
