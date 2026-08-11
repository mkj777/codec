using Avalonia;
using Avalonia.Controls;
using LibVLCSharp.Shared;

namespace Codec.Media;

public sealed partial class CodecVideoView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private LibVLCSharp.Shared.Media? _media;
    private bool _disposed;

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
    }

    public void Play() => _mediaPlayer?.Play();

    public void Pause() => _mediaPlayer?.Pause();

    public void Stop() => _mediaPlayer?.Stop();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposePlayer();
        base.OnDetachedFromVisualTree(e);
    }

    private void ApplySource()
    {
        if (_disposed || _libVlc == null || _mediaPlayer == null)
            return;

        _mediaPlayer.Stop();
        _mediaPlayer.Media = null;
        _media?.Dispose();
        _media = null;

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

    private void DisposePlayer()
    {
        if (_disposed)
            return;

        _disposed = true;
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _media?.Dispose();
        _libVlc?.Dispose();
        _mediaPlayer = null;
        _media = null;
        _libVlc = null;
    }
}
