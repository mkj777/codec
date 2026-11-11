using Codec.Helpers;
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
        private const string LibraryFileName = "library.json";

        private readonly string _baseDirectory;
        private readonly JsonFileStore _store;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <param name="baseDirectory">Where library.json lives. Defaults to the per-user app data folder.</param>
        public LibraryStorageService(string? baseDirectory = null)
        {
            _baseDirectory = baseDirectory ?? GetDefaultBaseDirectory();
            _store = new JsonFileStore(Path.Combine(_baseDirectory, LibraryFileName));
        }

        public static string GetDefaultBaseDirectory()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataFolderName);

        /// <summary>What the most recent <see cref="LoadAsync"/> found on disk.</summary>
        public StorageLoadOutcome LastLoadOutcome => _store.LastLoadOutcome;

        /// <summary>The error of the most recent write, or null when it succeeded.</summary>
        public Exception? LastSaveError => _store.LastSaveError;

        /// <summary>Raised on a background thread when a library write fails.</summary>
        public event EventHandler<Exception>? SaveFailed
        {
            add => _store.SaveFailed += value;
            remove => _store.SaveFailed -= value;
        }

        public string EnsureStorageInitialized()
        {
            Directory.CreateDirectory(_baseDirectory);
            return _baseDirectory;
        }

        public string GetLibraryPathForDiagnostics()
        {
            EnsureStorageInitialized();
            return _store.Path;
        }

        /// <summary>
        /// Saves the library. The games are serialized before this returns, so later changes to
        /// them do not leak into this save; the disk write runs in the background. Saves requested
        /// later always win. Never throws; false means this state did not reach disk.
        /// </summary>
        public Task<bool> SaveAsync(IEnumerable<Game> games)
        {
            byte[] content;
            try
            {
                var snapshot = RiotGameDuplicateHelper.DeduplicateByIdentity(games);
                content = JsonSerializer.SerializeToUtf8Bytes(snapshot, _jsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to serialize library: {ex.Message}");
                return Task.FromResult(false);
            }

            return _store.WriteAsync(content);
        }

        /// <summary>Completes once every save requested so far has finished.</summary>
        public Task FlushAsync() => _store.FlushAsync();

        /// <summary>Blocks until pending saves finish or the timeout passes.</summary>
        public bool Flush(TimeSpan timeout) => _store.Flush(timeout);

        /// <summary>
        /// Loads the library. Returns an empty list both for a fresh install and for an
        /// unreadable file with no usable backup; <see cref="LastLoadOutcome"/> tells them apart.
        /// An unreadable file is set aside under a ".corrupt-" name, never overwritten.
        /// </summary>
        public async Task<List<Game>> LoadAsync()
        {
            EnsureStorageInitialized();
            Debug.WriteLine($"Library load path: {_store.Path}");

            var games = await Task.Run(() => _store.Load<List<Game>>(_jsonOptions)).ConfigureAwait(false)
                ?? new List<Game>();
            games.RemoveAll(game => game is null);
            foreach (var game in games)
            {
                game.ImportedFrom = PlatformSourceNames.NormalizeImportSource(game.ImportedFrom);
            }

            return RiotGameDuplicateHelper.DeduplicateByIdentity(games);
        }
    }
}
