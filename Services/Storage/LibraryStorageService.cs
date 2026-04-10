using Codec.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Storage
{
    public class LibraryStorageService
    {
        public const string AppDataFolderName = "Codec Game Library";
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private string GetBaseDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataFolderName);
        }

        private string GetLibraryPath()
        {
            string dir = EnsureStorageInitialized();
            return Path.Combine(dir, "library.json");
        }

        public string EnsureStorageInitialized()
        {
            string dir = GetBaseDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string GetLibraryPathForDiagnostics() => GetLibraryPath();

        public async Task SaveAsync(IEnumerable<Game> games)
        {
            var snapshot = games.ToList();
            try
            {
                await _saveGate.WaitAsync();
                string path = GetLibraryPath();
                await using var fs = File.Create(path);
                await JsonSerializer.SerializeAsync(fs, snapshot, _jsonOptions);
                Debug.WriteLine($"Library saved to {path}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save library: {ex.Message}");
            }
            finally
            {
                _saveGate.Release();
            }
        }

        public async Task<List<Game>> LoadAsync()
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

        public async Task ResetAsync()
        {
            try
            {
                await _saveGate.WaitAsync();
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
            finally
            {
                _saveGate.Release();
            }
        }
    }
}
