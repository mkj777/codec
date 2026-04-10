using Codec.Services.Scanning.Scanners;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Codec.Services.Scanning
{
    /// <summary>
    /// Central catalog of well-known system/utility applications that should never be
    /// treated as valid games. Keeping this list in one place makes it trivial to extend
    /// and keeps the scan pipeline fast by filtering these entries up front.
    /// </summary>
    public static class NonGameSoftwareCatalog
    {
        private static readonly HashSet<string> ExactNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "PowerShell",
            "Windows PowerShell",
            "Google",
            "Google Chrome",
            "GoogleUpdate",
            "Google Updater (x64)",
            "Chrome",
            "Mozilla Maintenance Service",
            "Microsoft Office",
            "Microsoft Office 15",
            "Microsoft Office Click-to-Run",
            "Microsoft SDKs",
            "Microsoft .NET",
            "Microsoft .NET Framework",
            "Microsoft .NET Runtime",
            "Microsoft Windows",
            "Microsoft Windows Defender",
            "Windows Defender",
            "Windows Photo Viewer",
            "Windows Sidebar",
            "Windows Kits",
            "Windows Media Player",
            "Windows Mail",
            "VideoLAN",
            "VLC",
            "VLC media player",
            "System Volume Information",
            "$RECYCLE.BIN",
            "KeePassXC",
            "LGHUB",
            "Logitech G HUB",
            "Mullvad VPN",
            "OBS Studio",
            "obs-studio",
            "FanControl",
            "Overwolf",
            "Riot Vanguard",
            "Vanguard Client",
            "Radmin VPN",
            "RedHat",
            "Podman",
            "CPU-Z",
            "CPUID",
            "Black Tree Gaming Ltd",
            "FL Cloud Plugins",
            "FL Studio",
            "Image-Line",
            "PawnIO",
            "Easy Anti-Cheat Service (EOS)",
            "Easy Anti-Cheat Service",
            "EasyAntiCheat",
            "Epic Games Launcher",
            "Epic Games Store",
            "Epic Games",
            "InstallShield Installation Information",
            "Microsoft Visual C++",
            "MSBuild",
            "MSI Driver Utility Installer",
            "Electronic Arts",
            "EA Desktop",
            "EA App",
            "EA Javelin Anticheat",
            "GOG Galaxy",
            "Ubisoft Connect"
        };

        private static readonly string[] VendorTokens =
        {
            "anticheat",
            "driver",
            "runtime",
            "redistributable",
            "launcher",
            "updater",
            "maintenance",
            "vpn",
            "studio",
            "cloud plugin",
            "browser",
            "desktop",
            "sdk",
            "service",
            "vanguard",
            "utility"
        };

        private static readonly string[] PathIndicators =
        {
            "\\windows defender",
            "\\windowsapps",
            "\\easyanticheat",
            "\\ea anticheat",
            "\\vanguard",
            "\\riot vanguard",
            "\\powershell",
            "\\mullvad",
            "\\steelseries",
            "\\fancontrol",
            "\\google\\update",
            "\\cpuz",
            "\\cpuid",
            "\\videolan",
            "\\obs-studio",
            "\\overwolf",
            "\\radmin vpn",
            "\\epic games launcher",
            "\\gog galaxy",
            "\\system volume information"
        };

        public static bool IsNonGameCandidate(GameCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name) && string.IsNullOrWhiteSpace(candidate.FolderPath))
            {
                return false;
            }

            if (IsNonGameName(candidate.Name))
            {
                return true;
            }

            return IsNonGamePath(candidate.FolderPath);
        }

        public static bool IsNonGameDirectory(string directoryName, string fullPath)
        {
            if (IsNonGameName(directoryName))
            {
                return true;
            }

            return IsNonGamePath(fullPath);
        }

        private static bool IsNonGameName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string trimmed = name.Trim();
            if (ExactNames.Contains(trimmed))
            {
                return true;
            }

            string normalized = Normalize(trimmed);
            return VendorTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNonGamePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = Normalize(path);
            return PathIndicators.Any(indicator => normalized.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string value)
        {
            return value
                .Replace('?', ' ')
                .Replace('?', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Trim()
                .ToLowerInvariant();
        }
    }
}
