using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.Services.Storage
{
    /// <summary>What the last load of a stored file found.</summary>
    public enum StorageLoadOutcome
    {
        /// <summary>Nothing has been loaded yet.</summary>
        NotLoaded,
        /// <summary>The file was read normally.</summary>
        Loaded,
        /// <summary>There is no file yet, which is a fresh install.</summary>
        Missing,
        /// <summary>The file was unreadable; it was set aside and the previous generation was used.</summary>
        RecoveredFromBackup,
        /// <summary>The file was unreadable and no usable previous generation existed; defaults were used.</summary>
        Unreadable
    }

    /// <summary>
    /// One JSON file on disk with crash-safe writes and one previous generation kept as ".bak".
    ///
    /// Writes go to a temp file that is flushed to disk and then swapped in, so the target is
    /// always either the old or the new content, never a partial one. An unreadable file is
    /// renamed aside and never deleted or rotated into the backup. Saves are coalesced on one
    /// background writer so the newest requested content always ends up on disk last.
    /// </summary>
    public sealed class JsonFileStore
    {
        private const int IoAttempts = 3;
        private const string CorruptMarker = ".corrupt-";

        private readonly object _lock = new();
        private readonly List<(long Sequence, TaskCompletionSource<bool> Completion)> _waiters = new();
        private byte[]? _pendingContent;
        private long _requestedSequence;
        private bool _draining;

        // A target this session has not read or written could be a leftover bad file, and
        // File.Replace would rotate it into the backup over a good generation.
        private bool _targetVerified;
        private bool _targetSetAside;
        private Exception? _writeBlockedReason;

        public JsonFileStore(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
        }

        public string Path { get; }
        public string BackupPath => Path + ".bak";
        private string TempPath => Path + ".tmp";

        public StorageLoadOutcome LastLoadOutcome { get; private set; }
        public Exception? LastSaveError { get; private set; }

        /// <summary>Raised on a background thread when a write fails.</summary>
        public event EventHandler<Exception>? SaveFailed;

        /// <summary>
        /// Reads the file, or its backup when the file is unreadable. Returns default when
        /// nothing usable exists; <see cref="LastLoadOutcome"/> says which case applied.
        /// </summary>
        public T? Load<T>(JsonSerializerOptions options) where T : class
        {
            lock (_lock)
            {
                var (outcome, value) = LoadCore<T>(options);
                LastLoadOutcome = outcome;
                return value;
            }
        }

        private (StorageLoadOutcome, T?) LoadCore<T>(JsonSerializerOptions options) where T : class
        {
            var main = TryRead<T>(Path, options, out T? value);
            if (main == ReadResult.Ok)
            {
                _targetVerified = true;
                return (StorageLoadOutcome.Loaded, value);
            }

            if (main == ReadResult.Corrupt)
            {
                // A bad file that could not be preserved must not be written over: it may be
                // the only copy someone can still recover by hand.
                if (TrySetAside(Path))
                    _targetSetAside = true;
                else
                    _writeBlockedReason = new IOException($"Could not set aside the unreadable file {Path}.");
            }

            var backup = TryRead<T>(BackupPath, options, out T? backupValue);
            if (backup == ReadResult.Ok)
                return (StorageLoadOutcome.RecoveredFromBackup, backupValue);

            // A crash between the two renames inside File.Replace leaves only the backup, so a
            // missing target is only a fresh install when there is no backup either.
            if (backup == ReadResult.Corrupt)
            {
                if (!TrySetAside(BackupPath))
                    _writeBlockedReason = new IOException($"Could not set aside the unreadable file {BackupPath}.");
                return (StorageLoadOutcome.Unreadable, null);
            }

            return main == ReadResult.Missing && !_targetSetAside && _writeBlockedReason is null
                ? (StorageLoadOutcome.Missing, null)
                : (StorageLoadOutcome.Unreadable, null);
        }

        /// <summary>
        /// Queues already serialized content for writing. Content queued later always wins; a
        /// queued write that is superseded before it starts is skipped and completes with the
        /// result of the write that replaced it. Never throws; false means the content did not
        /// reach disk.
        /// </summary>
        public Task<bool> WriteAsync(byte[] content)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool startDrain = false;
            lock (_lock)
            {
                long sequence = ++_requestedSequence;
                _pendingContent = content;
                _waiters.Add((sequence, completion));
                if (!_draining)
                {
                    _draining = true;
                    startDrain = true;
                }
            }

            if (startDrain)
                _ = Task.Run(Drain);

            return completion.Task;
        }

        /// <summary>Completes once every write queued so far has finished.</summary>
        public Task FlushAsync()
        {
            lock (_lock)
                return _waiters.Count == 0 ? Task.CompletedTask : _waiters[^1].Completion.Task;
        }

        /// <summary>
        /// Blocks until every queued write has finished or the timeout passes. Safe on the UI
        /// thread because the writer never needs it.
        /// </summary>
        public bool Flush(TimeSpan timeout) => FlushAsync().Wait(timeout);

        private void Drain()
        {
            while (true)
            {
                byte[] content;
                long sequence;
                lock (_lock)
                {
                    if (_pendingContent is null)
                    {
                        _draining = false;
                        return;
                    }

                    content = _pendingContent;
                    sequence = _requestedSequence;
                    _pendingContent = null;
                }

                Exception? error = null;
                try
                {
                    WriteToDisk(content);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                List<TaskCompletionSource<bool>> finished = new();
                lock (_lock)
                {
                    LastSaveError = error;
                    // Ascending, so a flush awaiting the newest waiter sees all older ones done.
                    foreach (var waiter in _waiters)
                    {
                        if (waiter.Sequence <= sequence)
                            finished.Add(waiter.Completion);
                    }
                    _waiters.RemoveAll(waiter => waiter.Sequence <= sequence);
                }

                if (error is not null)
                {
                    Debug.WriteLine($"Failed to write {Path}: {error.Message}");
                    try
                    {
                        SaveFailed?.Invoke(this, error);
                    }
                    catch (Exception handlerError)
                    {
                        Debug.WriteLine($"SaveFailed handler threw: {handlerError.Message}");
                    }
                }

                foreach (var waiter in finished)
                    waiter.TrySetResult(error is null);
            }
        }

        private void WriteToDisk(byte[] content)
        {
            bool targetVerified;
            lock (_lock)
            {
                if (_writeBlockedReason is not null)
                    throw new IOException("Writing is blocked because an unreadable file could not be preserved.", _writeBlockedReason);
                targetVerified = _targetVerified;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            if (!targetVerified && File.Exists(Path) && TryValidate(Path) != ReadResult.Ok)
            {
                if (!TrySetAside(Path))
                    throw new IOException($"Could not set aside the unreadable file {Path}.");
            }

            Retry(() =>
            {
                if (File.Exists(Path))
                    File.Replace(TempPath, Path, BackupPath, ignoreMetadataErrors: true);
                else
                    File.Move(TempPath, Path, overwrite: false);
            });

            lock (_lock)
                _targetVerified = true;
        }

        private enum ReadResult { Ok, Missing, Corrupt }

        private static ReadResult TryRead<T>(string path, JsonSerializerOptions options, out T? value) where T : class
        {
            value = null;
            if (!TryReadBytes(path, out byte[]? bytes))
                return ReadResult.Corrupt;
            if (bytes is null)
                return ReadResult.Missing;
            if (bytes.Length == 0)
                return ReadResult.Corrupt;

            try
            {
                // Nothing in Codec ever writes a bare null, so it can only be damage.
                value = JsonSerializer.Deserialize<T>(bytes, options);
                return value is null ? ReadResult.Corrupt : ReadResult.Ok;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                Debug.WriteLine($"Unreadable JSON in {path}: {ex.Message}");
                return ReadResult.Corrupt;
            }
        }

        private static ReadResult TryValidate(string path)
        {
            if (!TryReadBytes(path, out byte[]? bytes))
                return ReadResult.Corrupt;
            if (bytes is null)
                return ReadResult.Missing;
            if (bytes.Length == 0)
                return ReadResult.Corrupt;

            try
            {
                using var document = JsonDocument.Parse(bytes);
                return document.RootElement.ValueKind == JsonValueKind.Null ? ReadResult.Corrupt : ReadResult.Ok;
            }
            catch (JsonException)
            {
                return ReadResult.Corrupt;
            }
        }

        // Returns true with null bytes when the file does not exist, and false when it exists
        // but could not be read even after a few attempts (antivirus and indexers hold short locks).
        private static bool TryReadBytes(string path, out byte[]? bytes)
        {
            bytes = null;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        return true;
                    bytes = File.ReadAllBytes(path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= IoAttempts)
                    {
                        Debug.WriteLine($"Could not read {path}: {ex.Message}");
                        return false;
                    }

                    Thread.Sleep(50 * attempt);
                }
            }
        }

        private static bool TrySetAside(string path)
        {
            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string destination = path + CorruptMarker + stamp;
                for (int suffix = 1; File.Exists(destination); suffix++)
                    destination = path + CorruptMarker + stamp + "-" + suffix.ToString(CultureInfo.InvariantCulture);

                Retry(() => File.Move(path, destination, overwrite: false));
                Debug.WriteLine($"Set aside unreadable file {path} as {destination}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not set aside {path}: {ex.Message}");
                return false;
            }
        }

        private static void Retry(Action action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < IoAttempts && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
    }
}
