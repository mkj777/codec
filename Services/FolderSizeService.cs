using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Codec.Services
{
    public static class FolderSizeService
    {
        public static Task<long> CalculateAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return Task.FromResult(0L);
            }

            return Task.Run(() => Calculate(folderPath));
        }

        private static long Calculate(string folderPath)
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(folderPath);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
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
