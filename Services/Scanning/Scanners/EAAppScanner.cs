using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Codec.Services.Scanning.Scanners
{
    /// <summary>
    /// EA App launcher integration - Registry-based detection
    /// </summary>
    public class EAAppScanner : PlatformScanner
    {
        public override string PlatformName => "EA App";

        public override Task<List<GameCandidate>> ScanAsync(IProgress<string>? progress = null)
        {
            var candidates = new List<GameCandidate>();

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\EA Games");
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var gameKey = key.OpenSubKey(subKeyName);
                        var installDir = gameKey?.GetValue("Install Dir") as string;

                        if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                        {
                            candidates.Add(new GameCandidate(subKeyName, installDir, "EA App"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"? Error scanning EA App: {ex.Message}");
            }

            return Task.FromResult(candidates);
        }
    }
}
