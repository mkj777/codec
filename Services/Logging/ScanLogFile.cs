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
        private static StreamWriter? _writer;
        private static string? _currentPath;

        public static string? CurrentPath => _currentPath;

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
                    _currentPath = Path.Combine(dir, $"scan-{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
                    _writer = new StreamWriter(_currentPath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _writer.WriteLine($"=== Scan log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ScanLogFile: failed to begin session: {ex.Message}");
                    _writer = null;
                    _currentPath = null;
                }
            }
        }

        public static void Write(string text)
        {
            lock (_gate)
            {
                if (_writer is null) return;
                try
                {
                    _writer.WriteLine(text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ScanLogFile: write failed: {ex.Message}");
                }
            }
        }

        public static void EndSession()
        {
            lock (_gate) { EndSession_NoLock(); }
        }

        private static void EndSession_NoLock()
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // ignored
            }
            _writer = null;
            _currentPath = null;
        }
    }
}
