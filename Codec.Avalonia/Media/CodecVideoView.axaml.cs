using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Lucide.Avalonia;

namespace Codec.Media;

/// <summary>
/// A video surface with the transport the WinUI build had: play, pause and seek, and no
/// autoplay unless it is asked for.
///
/// LibVLC raises its events on its own threads, so everything that touches the tree is
/// posted back to the UI one.
/// </summary>
public sealed partial class CodecVideoView : UserControl
{
    private const long SliderSteps = 1000;

    /// <summary>
    /// One engine for the process. Creating it loads and scans the plugin folder, which is too
    /// slow to repeat each time a trailer comes into view; the players stay one per view.
    /// </summary>
    private static readonly Lazy<LibVLC> SharedLibVlc = new(() =>
    {
        Core.Initialize();
        // Steam serves newer trailers as DASH, and the adaptive demuxer starts low and steps up
        // a couple of seconds in. Each step is a new decoder asking for a new video output
        // while the old one still holds the embedded window, and libvlc answers that by
        // opening a window of its own. Taking the best representation from the start keeps
        // it to one output for the whole clip.
        return new LibVLC("--no-video-title-show", "--adaptive-logic=highest");
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private LibVLCSharp.Shared.Media? _media;
    private CancellationTokenSource? _parseCancellation;
    private bool _disposed;

    /// <summary>True while the slider is being dragged, so the player stops writing to it.</summary>
    private bool _scrubbing;

    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<CodecVideoView, Uri?>(nameof(Source));

    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<CodecVideoView, bool>(nameof(AutoPlay));

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    static CodecVideoView()
    {
        SourceProperty.Changed.AddClassHandler<CodecVideoView>((view, _) => view.ApplySource());
        AutoPlayProperty.Changed.AddClassHandler<CodecVideoView>((view, _) => view.ApplyAutoPlay());
    }

    public CodecVideoView()
    {
        InitializeComponent();
        _libVlc = SharedLibVlc.Value;
        _mediaPlayer = new MediaPlayer(_libVlc);

        // The surface hands the player its window handle when the player is assigned, but only
        // once it is initialised and has a native window, and it has neither in this
        // constructor. Assigned here, the player never gets a handle and VLC opens a window of
        // its own the moment it plays. Both events fire later, in no fixed order; the last one
        // finds both conditions met.
        VideoSurface.AttachedToVisualTree += (_, _) => BindSurface();
        VideoSurface.Initialized += (_, _) => BindSurface();

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlayStateChanged;
        _mediaPlayer.Paused += OnPlayStateChanged;
        _mediaPlayer.Stopped += OnPlayStateChanged;
        _mediaPlayer.EndReached += OnPlayStateChanged;

        // The thumb marks its own press and release as handled, so a drag would never reach
        // a plain handler on the slider; a click on the track would. These listen past that.
        PositionSlider.AddHandler(PointerPressedEvent, PositionSlider_PointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, PositionSlider_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void BindSurface()
    {
        if (_disposed)
            return;

        // Through null first: the setter ignores the same player twice, and the surface may
        // have been given a fresh native window since the last time.
        VideoSurface.MediaPlayer = null;
        VideoSurface.MediaPlayer = _mediaPlayer;
    }

    public void Play() => _mediaPlayer?.Play();

    public void Pause()
    {
        if (_mediaPlayer?.IsPlaying == true)
            _mediaPlayer.Pause();
    }

    public void Stop()
    {
        _mediaPlayer?.Stop();
        ResetTransport();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposePlayer();
        base.OnDetachedFromVisualTree(e);
    }

    private void PlayPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null)
            return;

        // A clip that has run to the end has no position left to resume from, so the button
        // starts it again rather than doing nothing.
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else if (_mediaPlayer.Media != null)
            _mediaPlayer.Play();
    }

    private void PositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
        => _scrubbing = true;

    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _scrubbing = false;

        if (_mediaPlayer is not { Length: > 0 } player)
            return;

        player.Time = (long)(PositionSlider.Value / SliderSteps * player.Length);
        ElapsedText.Text = FormatTime(player.Time);
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _scrubbing || _mediaPlayer is not { Length: > 0 } player)
                return;

            PositionSlider.Value = (double)e.Time / player.Length * SliderSteps;
            ElapsedText.Text = FormatTime(e.Time);
        });

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                DurationText.Text = FormatTime(e.Length);
        });

    private void OnPlayStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;

            PlayPauseGlyph.Kind = _mediaPlayer?.IsPlaying == true
                ? LucideIconKind.Pause
                : LucideIconKind.Play;
        });

    private void ApplySource()
    {
        if (_disposed || _libVlc == null || _mediaPlayer == null)
            return;

        _mediaPlayer.Stop();
        _mediaPlayer.Media = null;
        ReleaseMedia();
        ResetTransport();

        if (Source == null)
            return;

        _media = new LibVLCSharp.Shared.Media(_libVlc, Source);
        _mediaPlayer.Media = _media;
        _parseCancellation = new CancellationTokenSource();
        _ = ShowDurationAsync(_media, _parseCancellation.Token);
        if (AutoPlay)
            _mediaPlayer.Play();
    }

    /// <summary>
    /// The length is only known once the container has been read, which playback does on its
    /// own. Reading it ahead means the clock shows how long the trailer is before Play.
    /// </summary>
    private async Task ShowDurationAsync(LibVLCSharp.Shared.Media media, CancellationToken cancellation)
    {
        try
        {
            await media.Parse(MediaParseOptions.ParseNetwork, timeout: 8000, cancellationToken: cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Video] parse failed: {exception.Message}");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || !ReferenceEquals(media, _media) || media.Duration <= 0)
                return;

            DurationText.Text = FormatTime(media.Duration);
        });
    }

    private void ApplyAutoPlay()
    {
        if (AutoPlay && Source != null)
            Play();
        else
            Pause();
    }

    private void ResetTransport()
    {
        if (_disposed)
            return;

        PositionSlider.Value = 0;
        ElapsedText.Text = FormatTime(0);
        PlayPauseGlyph.Kind = LucideIconKind.Play;
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void DisposePlayer()
    {
        if (_disposed)
            return;

        _disposed = true;

        // The surface destroys its native child on a later dispatcher pass and clears the
        // player's window handle as it goes. Handing it null now, while the player is still
        // alive, is what keeps that late call off a released libvlc object.
        VideoSurface.MediaPlayer = null;

        if (_mediaPlayer != null)
        {
            _mediaPlayer.TimeChanged -= OnTimeChanged;
            _mediaPlayer.LengthChanged -= OnLengthChanged;
            _mediaPlayer.Playing -= OnPlayStateChanged;
            _mediaPlayer.Paused -= OnPlayStateChanged;
            _mediaPlayer.Stopped -= OnPlayStateChanged;
            _mediaPlayer.EndReached -= OnPlayStateChanged;
        }

        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        ReleaseMedia();
        // The engine is shared and lives as long as the process.
        _mediaPlayer = null;
        _libVlc = null;
    }

    /// <summary>Drops the current media and the parse that may still be reading it.</summary>
    private void ReleaseMedia()
    {
        _parseCancellation?.Cancel();
        _parseCancellation?.Dispose();
        _parseCancellation = null;
        _media?.Dispose();
        _media = null;
    }
}
