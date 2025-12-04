using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Codec.Services
{
    internal static class GameContentHeuristics
    {
        private static readonly string[] UtilityNameKeywords =
        {
            "driver", "runtime", "redistributable", "support", "helper", "updater",
            "launcher", "service", "codec", "editor", "tool", "tools", "benchmark",
            "works", "manager", "daemon", "patch", "repair", "diagnostic", "viewer",
            "framework", "middleware", "sdk", "studio", "setup", "install", "installer",
            "uninstall", "config", "utility", "update", "crashhandler", "vc runtime",
            "dxsetup", "redist", "vcredist", "host", "monitor", "assistant"
        };

        private static readonly string[] UtilityPathKeywords =
        {
            "driver", "drivers", "support", "supportassist", "intel", "nvidia", "amd",
            "redistributable", "redist", "dotnet", "vcredist", "commonredist", "prereq",
            "tools", "tool", "utilities", "utility", "updater", "updaters", "launcher",
            "anticheat", "eac", "battleye", "crashhandler", "unrealengine", "engine",
            "benchmark", "firmware"
        };

        private static readonly string[] TrustedGamePathMarkers =
        {
            "steamapps\\common",
            "steamapps/common",
            "gog galaxy\\games",
            "gog galaxy/games",
            "epic games",
            "ubisoft game launcher\\games",
            "origin games",
            "ea games",
            "battlenet\\games",
            "rockstar games\\launcher",
            "xboxgames",
            "windowsapps"
        };

        public static bool ShouldIgnoreCandidate(string? displayName, string? folderPath, string source, bool hasSteamAppId)
        {
            if (hasSteamAppId)
            {
                return false;
            }

            if (IsTrustedGameInstallPath(folderPath))
            {
                return false;
            }

            bool nameHit = NameMatchesUtility(displayName);
            bool pathHit = PathMatchesUtility(folderPath);

            if (nameHit && pathHit)
            {
                return true;
            }

            if (nameHit && source.Equals("Heuristic Scan", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public static bool NameMatchesUtility(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string normalized = NormalizeForComparison(name);
            return UtilityNameKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        public static bool PathMatchesUtility(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = path.ToLowerInvariant();
            return UtilityPathKeywords.Any(keyword => normalized.Contains(keyword));
        }

        public static bool IsTrustedGameInstallPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = path.ToLowerInvariant();
            return TrustedGamePathMarkers.Any(marker => normalized.Contains(marker));
        }

        private static string NormalizeForComparison(string value)
        {
            string lower = value.ToLowerInvariant();
            lower = Regex.Replace(lower, @"[^a-z0-9\s]", " ");
            return Regex.Replace(lower, @"\s+", " ").Trim();
        }
    }
}
