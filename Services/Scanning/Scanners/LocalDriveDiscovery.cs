using System;
using System.Collections.Generic;
using System.IO;

namespace Codec.Services.Scanning.Scanners
{
    internal static class LocalDriveDiscovery
    {
        public static IEnumerable<DriveInfo> GetReadyNonNetworkDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { yield break; }

            foreach (var drive in drives)
            {
                if (!IsScannableDriveType(drive.DriveType))
                    continue;

                bool ready;
                try { ready = drive.IsReady; }
                catch { continue; }

                if (ready)
                    yield return drive;
            }
        }

        internal static bool IsScannableDriveType(DriveType driveType) =>
            driveType is DriveType.Fixed or DriveType.Removable;
    }
}
