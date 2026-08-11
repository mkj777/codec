using Codec.Services.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Codec.Services.Logging
{
    public static class ScanLogFile
    {
        private const int MaxLogFiles = 10;

        private static readonly object _gate = new();
        private static readonly string[] ScannerOrder =
        {
            "Steam",
            "Epic Games",
            "Riot Games",
            "Heuristic Scan"
        };

        private static readonly List<string> _sessionEntries = new();
        private static readonly List<string> _summaryEntries = new();
        private static readonly Dictionary<string, List<string>> _addedEntries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> _rejectedEntries = new(StringComparer.OrdinalIgnoreCase);
        private static StreamWriter? _writer;
        private static string? _logPath;
        private static string? _baseDirectoryOverride;
        private static bool _isSessionActive;

        public static string? LogPath
        {
            get
            {
                lock (_gate) { return _logPath; }
            }
        }

        public static string? AddedPath => LogPath;
        public static string? RejectedPath => LogPath;

        public static bool IsSessionActive
        {
            get
            {
                lock (_gate) { return _isSessionActive; }
            }
        }

        public static void BeginSession()
        {
            lock (_gate)
            {
                EndSession_NoLock();

                try
                {
                    string dir = GetLogDirectory();
                    Directory.CreateDirectory(dir);

                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    _logPath = GetUniqueLogPath(dir, $"scan-{stamp}", ".log");

                    _writer = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _isSessionActive = true;

                    string header = $"=== Scan log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===";
                    _writer.WriteLine(header);
                    EnforceRetention_NoLock(dir, _logPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ScanLogFile: failed to begin session: {ex.Message}");
                    try { _writer?.Dispose(); } catch { /* ignored */ }
                    _writer = null;
                    _logPath = null;
                    _isSessionActive = false;
                    ClearBuffers_NoLock();
                }
            }
        }

        /// <summary>Buffer session-wide context before scanner-specific results.</summary>
        public static void WriteSession(string text)
        {
            lock (_gate)
            {
                if (_isSessionActive) _sessionEntries.Add(text);
            }
        }

        /// <summary>Buffer a line for the final scan summary.</summary>
        public static void WriteSummary(string text)
        {
            lock (_gate)
            {
                if (_isSessionActive) _summaryEntries.Add(text);
            }
        }

        public static void WriteAdded(string source, string text)
        {
            lock (_gate) { AddResult_NoLock(_addedEntries, source, text); }
        }

        public static void WriteRejected(string source, string text)
        {
            lock (_gate)
            {
                AddResult_NoLock(_rejectedEntries, source, text);
            }
        }

        public static void EndSession()
        {
            lock (_gate) { EndSession_NoLock(); }
        }

        private static void AddResult_NoLock(Dictionary<string, List<string>> entries, string source, string text)
        {
            if (!_isSessionActive) return;

            string key = string.IsNullOrWhiteSpace(source) ? "Other" : source.Trim();
            if (!entries.TryGetValue(key, out var sourceEntries))
            {
                sourceEntries = new List<string>();
                entries[key] = sourceEntries;
            }

            sourceEntries.Add(text);
        }

        private static void EndSession_NoLock()
        {
            if (_writer != null)
            {
                try
                {
                    foreach (string entry in _sessionEntries)
                    {
                        _writer.WriteLine(entry);
                    }

                    foreach (string source in GetOrderedSources_NoLock())
                    {
                        WriteScannerSection_NoLock(source);
                    }

                    _writer.WriteLine();
                    _writer.WriteLine("=== SCAN COMPLETE ===");
                    foreach (string entry in _summaryEntries)
                    {
                        _writer.WriteLine(entry);
                    }

                    _writer.WriteLine();
                    _writer.WriteLine($"=== Scan log ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ScanLogFile: finalize failed: {ex.Message}");
                }
            }

            try { _writer?.Dispose(); } catch { /* ignored */ }
            _writer = null;
            _logPath = null;
            _isSessionActive = false;
            ClearBuffers_NoLock();
        }

        private static IEnumerable<string> GetOrderedSources_NoLock()
        {
            var present = _addedEntries.Keys
                .Concat(_rejectedEntries.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string source in ScannerOrder)
            {
                yield return source;
                present.Remove(source);
            }

            foreach (string source in present.OrderBy(source => source, StringComparer.OrdinalIgnoreCase))
            {
                yield return source;
            }
        }

        private static void WriteScannerSection_NoLock(string source)
        {
            string heading = source.Equals("Heuristic Scan", StringComparison.OrdinalIgnoreCase)
                ? "HEURISTIC SCANNER"
                : $"{source.ToUpperInvariant()} SCANNER";

            _writer!.WriteLine();
            _writer.WriteLine($"=== {heading} ===");
            WriteResultGroup_NoLock("ADDED", _addedEntries, source);
            WriteResultGroup_NoLock("REJECTED / SKIPPED / FAILED", _rejectedEntries, source);
        }

        private static void WriteResultGroup_NoLock(string heading, Dictionary<string, List<string>> entries, string source)
        {
            _writer!.WriteLine($"--- {heading} ---");
            if (!entries.TryGetValue(source, out var sourceEntries) || sourceEntries.Count == 0)
            {
                _writer.WriteLine("(none)");
                return;
            }

            foreach (string entry in sourceEntries)
            {
                _writer.WriteLine(entry);
            }
        }

        private static void ClearBuffers_NoLock()
        {
            _sessionEntries.Clear();
            _summaryEntries.Clear();
            _addedEntries.Clear();
            _rejectedEntries.Clear();
        }

        private static string GetBaseDirectory()
        {
            return _baseDirectoryOverride
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LibraryStorageService.AppDataFolderName);
        }

        private static string GetLogDirectory()
        {
            return Path.Combine(GetBaseDirectory(), "Logs");
        }

        private static string GetUniqueLogPath(string directory, string fileNameWithoutExtension, string extension)
        {
            string path = Path.Combine(directory, fileNameWithoutExtension + extension);
            for (int i = 1; File.Exists(path); i++)
            {
                path = Path.Combine(directory, $"{fileNameWithoutExtension}-{i}{extension}");
            }

            return path;
        }

        private static void EnforceRetention_NoLock(string directory, string currentPath)
        {
            try
            {
                string normalizedCurrentPath = Path.GetFullPath(currentPath);
                var oldFiles = Directory.EnumerateFiles(directory, "*.log")
                    .Select(path => new FileInfo(path))
                    .Where(file => !string.Equals(Path.GetFullPath(file.FullName), normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int totalCount = oldFiles.Count + 1;
                foreach (FileInfo file in oldFiles)
                {
                    if (totalCount <= MaxLogFiles)
                    {
                        break;
                    }

                    try
                    {
                        file.Delete();
                        totalCount--;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ScanLogFile: failed to delete old log '{file.FullName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ScanLogFile: retention failed: {ex.Message}");
            }
        }

        internal static void SetBaseDirectoryForTests(string? baseDirectory)
        {
            lock (_gate)
            {
                EndSession_NoLock();
                _baseDirectoryOverride = baseDirectory;
            }
        }
    }
}
