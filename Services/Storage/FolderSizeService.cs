using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Codec.Services.Storage
{
    public readonly record struct FolderSizeResult(bool Success, long Size);

    public static class FolderSizeService
    {
        public static Task<long> CalculateAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return Task.FromResult(0L);
            }

            return Task.Run(() => Calculate(folderPath, cancellationToken), cancellationToken);
        }

        public static Task<FolderSizeResult> TryCalculateAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return Task.FromResult(new FolderSizeResult(false, 0L));

            return Task.Run(() => TryCalculate(folderPath, cancellationToken), cancellationToken);
        }

        private static FolderSizeResult TryCalculate(string folderPath, CancellationToken cancellationToken)
        {
            try
            {
                long total = 0;
                var pending = new Stack<string>();
                pending.Push(folderPath);

                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string current = pending.Pop();

                    foreach (string file in Directory.EnumerateFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        total += new FileInfo(file).Length;
                    }

                    foreach (string directory in Directory.EnumerateDirectories(current))
                        pending.Push(directory);
                }

                return new FolderSizeResult(true, total);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Folder size check failed IO: {ex.Message}");
                return new FolderSizeResult(false, 0L);
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Folder size check failed access: {ex.Message}");
                return new FolderSizeResult(false, 0L);
            }
        }

        private static long Calculate(string folderPath, CancellationToken cancellationToken)
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(folderPath);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }

                    foreach (var dir in Directory.EnumerateDirectories(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pending.Push(dir);
                    }
                }
                catch (IOException ex)
                {
                    Debug.WriteLine($"Folder size skipped IO: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Debug.WriteLine($"Folder size skipped access: {ex.Message}");
                }
            }

            return total;
        }
    }
}
