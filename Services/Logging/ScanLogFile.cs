using Codec.Services.Storage;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Codec.Services.Logging
{
    public static class ScanLogFile
    {
        private static readonly object _gate = new();
        private static StreamWriter? _addedWriter;
        private static StreamWriter? _rejectedWriter;
        private static string? _addedPath;
        private static string? _rejectedPath;

        public static string? AddedPath => _addedPath;
        public static string? RejectedPath => _rejectedPath;

        public static void BeginSession()
        {
            lock (_gate)
            {
                EndSession_NoLock();

                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        LibraryStorageService.AppDataFolderName);
                    Directory.CreateDirectory(dir);

                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    _addedPath = Path.Combine(dir, $"scan-added-{stamp}.log");
                    _rejectedPath = Path.Combine(dir, $"scan-rejected-{stamp}.log");

                    _addedWriter = new StreamWriter(_addedPath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _rejectedWriter = new StreamWriter(_rejectedPath, append: false, Encoding.UTF8) { AutoFlush = true };

                    string header = $"=== Scan log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===";
                    _addedWriter.WriteLine(header);
                    _rejectedWriter.WriteLine(header);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ScanLogFile: failed to begin session: {ex.Message}");
                    _addedWriter = null;
                    _rejectedWriter = null;
                    _addedPath = null;
                    _rejectedPath = null;
                }
            }
        }

        /// <summary>Write to both added + rejected logs (used for session-wide context like phase headers).</summary>
        public static void WriteSession(string text)
        {
            lock (_gate)
            {
                TryWrite(_addedWriter, text);
                TryWrite(_rejectedWriter, text);
            }
        }

        /// <summary>Write to the added-games log only.</summary>
        public static void WriteAdded(string text)
        {
            lock (_gate) { TryWrite(_addedWriter, text); }
        }

        /// <summary>Write to the rejected/denied/failed log only.</summary>
        public static void WriteRejected(string text)
        {
            lock (_gate) { TryWrite(_rejectedWriter, text); }
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
            try { _addedWriter?.Dispose(); } catch { /* ignored */ }
            try { _rejectedWriter?.Dispose(); } catch { /* ignored */ }
            _addedWriter = null;
            _rejectedWriter = null;
            _addedPath = null;
            _rejectedPath = null;
        }
    }
}
