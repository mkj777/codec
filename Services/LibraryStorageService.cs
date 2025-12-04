using Codec.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Codec.Services
{
    public static class LibraryStorageService
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static string GetBaseDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codec");
        }

        private static string GetLibraryPath()
        {
            string dir = EnsureStorageInitialized();
            return Path.Combine(dir, "library.json");
        }

        public static string EnsureStorageInitialized()
        {
            string dir = GetBaseDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetLibraryPathForDiagnostics() => GetLibraryPath();

        public static async Task SaveAsync(IEnumerable<Game> games)
        {
            try
            {
                string path = GetLibraryPath();
                await using var fs = File.Create(path);
                await JsonSerializer.SerializeAsync(fs, games, _jsonOptions);
                Debug.WriteLine($"Library saved to {path}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save library: {ex.Message}");
            }
        }

        public static async Task<List<Game>> LoadAsync()
        {
            try
            {
                string path = GetLibraryPath();
                Debug.WriteLine($"Library load path: {path}");
                if (!File.Exists(path))
                    return new List<Game>();

                await using var fs = File.OpenRead(path);
                var data = await JsonSerializer.DeserializeAsync<List<Game>>(fs, _jsonOptions);
                return data ?? new List<Game>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load library: {ex.Message}");
                return new List<Game>();
            }
        }

        public static async Task ResetAsync()
        {
            try
            {
                string dir = GetBaseDirectory();
                if (Directory.Exists(dir))
                {
                    await Task.Run(() => Directory.Delete(dir, recursive: true));
                }
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to reset library: {ex.Message}");
                throw;
            }
        }
    }
}
