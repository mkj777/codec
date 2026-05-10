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
        private static readonly List<string> _rejectedEntries = new();
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
                    _writer.WriteLine();
                    _writer.WriteLine("=== ADDED ===");

                    EnforceRetention_NoLock(dir, _logPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ScanLogFile: failed to begin session: {ex.Message}");
                    try { _writer?.Dispose(); } catch { /* ignored */ }
                    _writer = null;
                    _logPath = null;
                    _isSessionActive = false;
                    _rejectedEntries.Clear();
                }
            }
        }

        /// <summary>Write session-wide context into the first section of the scan log.</summary>
        public static void WriteSession(string text)
        {
            lock (_gate)
            {
                TryWrite(_writer, text);
            }
        }

        /// <summary>Write to the added-games section.</summary>
        public static void WriteAdded(string text)
        {
            lock (_gate) { TryWrite(_writer, text); }
        }

        /// <summary>Buffer rejected/denied/failed entries so they can be appended after added games.</summary>
        public static void WriteRejected(string text)
        {
            lock (_gate)
            {
                if (!_isSessionActive)
                {
                    return;
                }

                _rejectedEntries.Add(text);
            }
        }

        public static void EndSession()
        {
            lock (_gate) { EndSession_NoLock(); }
        }

        private static void TryWrite(StreamWriter? writer, string text)
        {
            if (writer is null) return;
            try
            {
                writer.WriteLine(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ScanLogFile: write failed: {ex.Message}");
            }
        }

        private static void EndSession_NoLock()
        {
            if (_writer != null)
            {
                try
                {
                    _writer.WriteLine();
                    _writer.WriteLine("=== REJECTED / SKIPPED / FAILED ===");

                    if (_rejectedEntries.Count == 0)
                    {
                        _writer.WriteLine("(none)");
                    }
                    else
                    {
                        foreach (string entry in _rejectedEntries)
                        {
                            _writer.WriteLine(entry);
                        }
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
