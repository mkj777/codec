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

        private static string GetLibraryPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codec");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "library.json");
        }

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
    }
}
