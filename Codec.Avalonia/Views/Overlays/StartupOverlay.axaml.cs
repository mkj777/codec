using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Codec.Views.Overlays;

public sealed partial class StartupOverlay : UserControl
{
    /// <summary>SPI_GETCLIENTAREAANIMATION, the signal WinUI's UISettings.AnimationsEnabled reads.</summary>
    private const uint ClientAreaAnimation = 0x1042;

    private const int SequenceMs = 2200;
    private const int ReducedSequenceMs = 450;
    private const int ExitMs = 300;

    private bool _played;

    public StartupOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Runs the launch sequence and leaves once the library is ready. The overlay is opaque, so
    /// it must come down whatever happens: a failed load is the library's problem to report, not
    /// a reason to strand the user behind a black screen.
    /// </summary>
    public async Task PlayAsync(Task libraryReady)
    {
        if (_played)
            return;

        _played = true;
        bool animate = AreAnimationsEnabled();

        if (animate)
        {
            Mosaic.Classes.Add("go");
            Brand.Classes.Add("go");
        }
        else
        {
            foreach (Control tile in Tiles.Children)
                tile.Opacity = 0.42;

            Brand.Opacity = 1;
        }

        try
        {
            await Task.WhenAll(
                Task.Delay(animate ? SequenceMs : ReducedSequenceMs),
                libraryReady);
        }
        catch
        {
            // Reported elsewhere. The overlay leaves either way.
        }

        if (animate)
        {
            Root.Classes.Add("out");
            await Task.Delay(ExitMs);
        }

        IsVisible = false;

        // The sequence never plays twice, and hidden is not gone: the nine covers would stay
        // decoded and bound to the library for the rest of the session. Detaching the tree
        // releases them.
        Content = null;
    }

    private static bool AreAnimationsEnabled()
    {
        try
        {
            int enabled = 1;
            // A failed call leaves the default in place, which is to animate.
            return !SystemParametersInfoW(ClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
        }
        catch (DllNotFoundException)
        {
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref int value, uint winIni);
}
