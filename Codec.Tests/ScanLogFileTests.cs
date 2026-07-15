using Codec.Services.Logging;
using Xunit;

namespace Codec.Tests
{
    [Collection("ScanLogFile")]
    public sealed class ScanLogFileTests
    {
        [Fact]
        public void BeginSession_CreatesSingleLogUnderLogsDirectory()
        {
            string baseDir = CreateTempBaseDirectory();
            try
            {
                ScanLogFile.SetBaseDirectoryForTests(baseDir);

                ScanLogFile.BeginSession();
                string logPath = Assert.IsType<string>(ScanLogFile.LogPath);
                ScanLogFile.EndSession();

                string expectedLogDir = Path.Combine(baseDir, "Logs");
                Assert.True(
                    string.Equals(expectedLogDir, Path.GetDirectoryName(logPath), StringComparison.OrdinalIgnoreCase),
                    $"Expected log directory '{expectedLogDir}', got '{Path.GetDirectoryName(logPath)}'.");
                Assert.Single(Directory.GetFiles(expectedLogDir, "*.log"));
                Assert.Empty(Directory.GetFiles(baseDir, "*.log"));
            }
            finally
            {
                ScanLogFile.SetBaseDirectoryForTests(null);
                DeleteDirectoryIfExists(baseDir);
            }
        }

        [Fact]
        public void EndSession_GroupsResultsByScannerAndFinishesWithSummary()
        {
            string baseDir = CreateTempBaseDirectory();
            try
            {
                ScanLogFile.SetBaseDirectoryForTests(baseDir);

                ScanLogFile.BeginSession();
                string logPath = Assert.IsType<string>(ScanLogFile.LogPath);
                ScanLogFile.WriteRejected("Heuristic Scan", "heuristic rejected");
                ScanLogFile.WriteAdded("Steam", "steam added");
                ScanLogFile.WriteAdded("Epic Games", "epic added");
                ScanLogFile.WriteSummary("Total time: 12.3s");
                ScanLogFile.EndSession();

                string text = File.ReadAllText(logPath);
                int steamIndex = text.IndexOf("=== STEAM SCANNER ===", StringComparison.Ordinal);
                int epicIndex = text.IndexOf("=== EPIC GAMES SCANNER ===", StringComparison.Ordinal);
                int riotIndex = text.IndexOf("=== RIOT GAMES SCANNER ===", StringComparison.Ordinal);
                int heuristicIndex = text.IndexOf("=== HEURISTIC SCANNER ===", StringComparison.Ordinal);
                int completeIndex = text.IndexOf("=== SCAN COMPLETE ===", StringComparison.Ordinal);

                Assert.True(steamIndex < epicIndex && epicIndex < riotIndex && riotIndex < heuristicIndex);
                Assert.True(heuristicIndex < completeIndex);
                Assert.True(text.IndexOf("steam added", StringComparison.Ordinal) < completeIndex);
                Assert.True(text.IndexOf("heuristic rejected", StringComparison.Ordinal) < completeIndex);
                Assert.True(text.IndexOf("Total time: 12.3s", StringComparison.Ordinal) > completeIndex);
            }
            finally
            {
                ScanLogFile.SetBaseDirectoryForTests(null);
                DeleteDirectoryIfExists(baseDir);
            }
        }

        [Fact]
        public void BeginSession_DeletesOldestLogWhenRetentionWouldBeExceeded()
        {
            string baseDir = CreateTempBaseDirectory();
            string logDir = Path.Combine(baseDir, "Logs");
            Directory.CreateDirectory(logDir);

            try
            {
                DateTime start = DateTime.UtcNow.AddDays(-1);
                for (int i = 0; i < 10; i++)
                {
                    string path = Path.Combine(logDir, $"scan-old-{i:00}.log");
                    File.WriteAllText(path, "old");
                    File.SetLastWriteTimeUtc(path, start.AddMinutes(i));
                }

                ScanLogFile.SetBaseDirectoryForTests(baseDir);

                ScanLogFile.BeginSession();
                string newLogName = Path.GetFileName(Assert.IsType<string>(ScanLogFile.LogPath));
                ScanLogFile.EndSession();

                var logFiles = Directory.GetFiles(logDir, "*.log")
                    .Select(Path.GetFileName)
                    .ToArray();

                Assert.Equal(10, logFiles.Length);
                Assert.Contains(newLogName, logFiles);
                Assert.DoesNotContain("scan-old-00.log", logFiles);
            }
            finally
            {
                ScanLogFile.SetBaseDirectoryForTests(null);
                DeleteDirectoryIfExists(baseDir);
            }
        }

        private static string CreateTempBaseDirectory()
        {
            return Path.Combine(Path.GetTempPath(), "CodecScanLogTests", Guid.NewGuid().ToString("N"));
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // best effort for temp files
            }
        }
    }

    [CollectionDefinition("ScanLogFile", DisableParallelization = true)]
    public sealed class ScanLogFileCollection
    {
    }
}
