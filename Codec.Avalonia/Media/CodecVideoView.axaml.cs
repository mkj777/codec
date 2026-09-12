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

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private LibVLCSharp.Shared.Media? _media;
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
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoSurface.MediaPlayer = _mediaPlayer;

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlayStateChanged;
        _mediaPlayer.Paused += OnPlayStateChanged;
        _mediaPlayer.Stopped += OnPlayStateChanged;
        _mediaPlayer.EndReached += OnPlayStateChanged;
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
        _media?.Dispose();
        _media = null;
        ResetTransport();

        if (Source == null)
            return;

        _media = new LibVLCSharp.Shared.Media(_libVlc, Source);
        _mediaPlayer.Media = _media;
        if (AutoPlay)
            _mediaPlayer.Play();
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
        _media?.Dispose();
        _libVlc?.Dispose();
        _mediaPlayer = null;
        _media = null;
        _libVlc = null;
    }
}
