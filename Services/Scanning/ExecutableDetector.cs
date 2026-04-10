using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Codec.Services.Scanning
{
    /// <summary>
    /// Heuristic executable detection funnel for identifying the correct game launcher
    /// within a game's installation directory.
    /// </summary>
    internal static class ExecutableDetector
    {
        /// <summary>
        /// The Detection Funnel: A step-by-step algorithm prioritizing reliability.
        /// </summary>
        public static string ExecuteDetectionFunnel(string gameFolderPath, string gameName)
        {
            if (!Directory.Exists(gameFolderPath))
            {
                Debug.WriteLine($"  ? Folder does not exist");
                return "";
            }

            Debug.WriteLine($"  === HEURISTIC FUNNEL: {gameName} ===");

            // Step 1: Recursive scan for all .exe files
            var allExeFiles = Directory.GetFiles(gameFolderPath, "*.exe", SearchOption.AllDirectories).ToList();
            Debug.WriteLine($"  [1/5] Recursive scan: {allExeFiles.Count} .exe files found");

            // Step 2: Initial exclusion (negative heuristic)
            var candidates = allExeFiles.Where(exe => !IsExcluded(exe)).ToList();
            Debug.WriteLine($"  [2/5] After exclusion: {candidates.Count} candidates remain");

            if (!candidates.Any())
            {
                Debug.WriteLine($"  ? No valid candidates");
                return "";
            }

            // Step 3: Score each candidate with weighted heuristic model
            var scoredCandidates = candidates
                .Select(exe => new { Path = exe, Score = CalculateHeuristicScore(exe, gameName, gameFolderPath, candidates) })
                .OrderByDescending(c => c.Score)
                .ToList();

            Debug.WriteLine($"  [3/5] Heuristic scoring complete");
            Debug.WriteLine($"  --- TOP 5 CANDIDATES ---");
            foreach (var candidate in scoredCandidates.Take(5))
            {
                Debug.WriteLine($"    [{candidate.Score:0000}] {Path.GetFileName(candidate.Path)}");
            }

            // Step 4: Advanced vector analysis (launcher detection, anti-cheat)
            var topCandidates = scoredCandidates.Take(3).Select(c => (c.Path, c.Score)).ToList();
            var finalCandidate = ApplyAdvancedVectorAnalysis(topCandidates, gameFolderPath);

            Debug.WriteLine($"  [4/5] Advanced analysis applied");
            Debug.WriteLine($"  ? SELECTED: {Path.GetFileName(finalCandidate.Path)} (Score: {finalCandidate.Score})");

            // Step 5: Return (caching happens in caller)
            return finalCandidate.Path;
        }

        private static bool IsExcluded(string exePath)
        {
            var fileName = Path.GetFileName(exePath).ToLowerInvariant();
            var directory = Path.GetDirectoryName(exePath)?.ToLowerInvariant() ?? "";

            var excludedKeywords = new[]
            {
                "uninstall", "setup", "redist", "dxsetup", "vcredist", "dotnet",
                "crashreporter", "activation", "support", "easyanticheat_setup",
                "eaanticheat.installer", "prereq", "directx", "battleye"
            };

            if (excludedKeywords.Any(kw => fileName.Contains(kw)))
                return true;

            var excludedDirs = new[] { "redist", "_commonredist", "support", "directx", "prerequisites", "installers", "tools", "docs" };
            if (excludedDirs.Any(dir => directory.Contains(dir)))
                return true;

            return false;
        }

        private static int CalculateHeuristicScore(string exePath, string gameName, string gameFolderPath, List<string> allCandidates)
        {
            int score = 0;
            var fileName = Path.GetFileNameWithoutExtension(exePath);
            var fileNameLower = fileName.ToLowerInvariant();
            var gameNameLower = gameName.ToLowerInvariant();
            var directory = Path.GetDirectoryName(exePath) ?? "";
            var relativePath = exePath.Replace(gameFolderPath, "").ToLowerInvariant();
            var isRootDirectory = directory == gameFolderPath;

            // === POSITIVE FACTORS ===

            // CRITICAL PRIORITY: Launcher in Root Directory (+60)
            if (fileNameLower.Contains("launcher") && isRootDirectory)
            {
                score += 60;
            }

            // Title Match (+50): Fuzzy matching with Levenshtein distance
            double similarity = CalculateSimilarity(fileNameLower, gameNameLower);
            if (similarity > 0.8) score += 50;
            else if (similarity > 0.6) score += 30;
            else if (similarity > 0.4) score += 15;

            // Engine Build Suffix (+40): -shipping.exe (Unreal Engine)
            if (fileNameLower.EndsWith("-shipping"))
                score += 40;

            // "Launcher" Keyword (+35): Often the correct entry point
            if (fileNameLower.Contains("launcher") && !isRootDirectory)
                score += 35;

            // Architecture Match (+30): x64/win64 on 64-bit system
            if (Environment.Is64BitOperatingSystem)
            {
                bool hasX64InName = fileNameLower.Contains("x64") || fileNameLower.Contains("win64") || fileNameLower.Contains("64bit");
                bool hasX64InPath = relativePath.Contains(@"\x64\") || relativePath.Contains(@"\x64vk\") || relativePath.Contains(@"win64");

                if (hasX64InName || hasX64InPath)
                    score += 30;
            }

            // "Game" Keyword (+20)
            if (fileNameLower.Contains("game"))
                score += 20;

            // Root Directory (+10)
            if (isRootDirectory)
                score += 10;

            // Standard Engine Directory (+40)
            if (relativePath.Contains(@"\binaries\win64") || relativePath.Contains(@"\bin\") || relativePath.Contains(@"\x64\"))
                score += 40;

            // Largest File Size (+25)
            try
            {
                var thisSize = new FileInfo(exePath).Length;
                var maxSize = allCandidates.Max(c => new FileInfo(c).Length);
                if (thisSize == maxSize) score += 25;
                else if (thisSize > 50 * 1024 * 1024) score += 15;
                else if (thisSize > 10 * 1024 * 1024) score += 10;
            }
            catch { }

            // === NEGATIVE FACTORS ===

            // "Editor" Keyword (-100)
            if (fileNameLower.Contains("editor"))
                score -= 100;

            // Generic Engine Names (-20)
            var genericNames = new[] { "ue4game", "unityplayer", "unitycrashandler" };
            if (genericNames.Any(name => fileNameLower == name))
                score -= 20;

            // Render API in Path (-10)
            var renderApiMarkers = new[] { "vk", "vulkan", "gl", "opengl", "dx11", "dx12" };
            if (renderApiMarkers.Any(api => relativePath.Contains(api)))
                score -= 10;

            // Config/Update Launchers in Tools/Utils directories (-20)
            if (fileNameLower.Contains("launcher") && (relativePath.Contains("tools") || relativePath.Contains("utils") || relativePath.Contains("config")))
                score -= 20;

            return score;
        }

        private static double CalculateSimilarity(string source, string target)
            => Helpers.StringSimilarity.Calculate(source, target);

        private static (string Path, int Score) ApplyAdvancedVectorAnalysis(List<(string Path, int Score)> topCandidates, string gameFolderPath)
        {
            var candidates = topCandidates.Select(c => (c.Path, c.Score)).ToList();

            var launcherCandidate = candidates.FirstOrDefault(c => System.IO.Path.GetFileNameWithoutExtension(c.Path).ToLowerInvariant().Contains("launcher"));

            bool hasAntiCheat = DetectAntiCheat(gameFolderPath);

            if (hasAntiCheat && launcherCandidate != default)
            {
                Debug.WriteLine($"    ? Anti-cheat detected, prioritizing launcher");
                return launcherCandidate;
            }

            return candidates.First();
        }

        private static bool DetectAntiCheat(string gameFolderPath)
        {
            try
            {
                var subDirs = Directory.GetDirectories(gameFolderPath, "*", SearchOption.TopDirectoryOnly);
                return subDirs.Any(dir => dir.ToLowerInvariant().Contains("easyanticheat") || dir.ToLowerInvariant().Contains("battleye"));
            }
            catch { return false; }
        }
    }
}
