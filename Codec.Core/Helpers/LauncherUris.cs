using System;

namespace Codec.Helpers
{
    /// <summary>
    /// The protocol URIs Codec hands to the other launchers.
    ///
    /// Pure string building with no view state, so it belongs beside the rest of the platform
    /// knowledge in Core rather than on the ViewModel. The test project references Core only,
    /// and these two are the reason it stopped compiling when the WinUI project was split.
    /// </summary>
    public static class LauncherUris
    {
        public static string EpicLaunch(string epicAppId) =>
            $"com.epicgames.launcher://apps/{Uri.EscapeDataString(epicAppId)}?action=launch&silent=true";

        public static string SteamInstall(int appId) => $"steam://install/{appId}";
    }
}
