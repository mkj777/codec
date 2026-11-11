using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace Codec.Media;

public sealed class CodecImage : Control
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// Room for the hover growth the styles apply after the decode: covers grow to 1.055 and
    /// media tiles to 1.04. A bitmap decoded for the resting size would be upscaled under them.
    /// </summary>
    private const double HoverHeadroom = 1.06;

    /// <summary>A resize re-columns the shelf on every frame; one re-decode once it settles is enough.</summary>
    private static readonly TimeSpan UpgradeDelay = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _loadCancellation;
    private LoadedImage? _loaded;
    private long _loadVersion;
    private Size? _lastAvailableSize;
    private TaskCompletionSource? _layoutSignal;
    private IDisposable? _pendingUpgrade;
    private TopLevel? _topLevel;

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<CodecImage, string?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CodecImage, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<Bitmap?> PlaceholderProperty =
        AvaloniaProperty.Register<CodecImage, Bitmap?>(nameof(Placeholder));

    /// <summary>
    /// Where the crop is anchored vertically, matching CSS background-position.
    /// 0.5 centres; 0.22 keeps the upper part of an overflowing image in view.
    /// </summary>
    public static readonly StyledProperty<double> VerticalBiasProperty =
        AvaloniaProperty.Register<CodecImage, double>(nameof(VerticalBias), 0.5);

    /// <summary>
    /// Landscape artwork shown whole rather than cropped. Steam falls back to
    /// capsule_616x353 and then header.jpg at 460x215 when a title has no
    /// 600x900 capsule, and cropping those to a 2:3 card cuts the logo out.
    /// The detection lives here because this control already holds the decoded
    /// bitmap and therefore its real dimensions.
    /// </summary>
    public static readonly StyledProperty<bool> LetterboxWideArtProperty =
        AvaloniaProperty.Register<CodecImage, bool>(nameof(LetterboxWideArt));

    /// <summary>
    /// Ask for the size the artwork actually needs instead of filling whatever it is given.
    /// A cover fills a card and wants the default; a logo has to end where the picture ends,
    /// or aligning it left leaves it floating in the middle of an invisible full-width box.
    /// Off by default so no existing use changes size.
    /// </summary>
    public static readonly StyledProperty<bool> IntrinsicSizeProperty =
        AvaloniaProperty.Register<CodecImage, bool>(nameof(IntrinsicSize));

    /// <summary>
    /// Drop the picture on screen the moment the source changes instead of when the new one has
    /// decoded. A recycled list row would otherwise show the previous item's image meanwhile.
    /// Off by default: a cover refreshed in place should not blink.
    /// </summary>
    public static readonly StyledProperty<bool> ClearOnSourceChangeProperty =
        AvaloniaProperty.Register<CodecImage, bool>(nameof(ClearOnSourceChange));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public Bitmap? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public double VerticalBias
    {
        get => GetValue(VerticalBiasProperty);
        set => SetValue(VerticalBiasProperty, value);
    }

    public bool LetterboxWideArt
    {
        get => GetValue(LetterboxWideArtProperty);
        set => SetValue(LetterboxWideArtProperty, value);
    }

    public bool IntrinsicSize
    {
        get => GetValue(IntrinsicSizeProperty);
        set => SetValue(IntrinsicSizeProperty, value);
    }

    public bool ClearOnSourceChange
    {
        get => GetValue(ClearOnSourceChangeProperty);
        set => SetValue(ClearOnSourceChangeProperty, value);
    }

    static CodecImage()
    {
        AffectsRender<CodecImage>(
            StretchProperty, PlaceholderProperty, VerticalBiasProperty, LetterboxWideArtProperty);
        SourceProperty.Changed.AddClassHandler<CodecImage>((control, _) =>
        {
            if (control.ClearOnSourceChange)
                control.ReplaceBitmap(null);
            control.BeginLoad();
        });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _lastAvailableSize = availableSize;
        if (IntrinsicSize)
            OnLayoutChanged();

        if (!IntrinsicSize)
            return base.MeasureOverride(availableSize);

        // Layout runs on the artwork's own size, never on the size it was decoded at, so a
        // smaller decode cannot move anything.
        Size? natural = _loaded?.NaturalSize ?? Placeholder?.Size;
        if (natural is not { Width: > 0, Height: > 0 } image)
            return default;

        return FitIntrinsic(image, availableSize);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Bitmap? image = _loaded?.Bitmap ?? Placeholder;
        if (image == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        Size natural = _loaded?.NaturalSize ?? image.Size;
        Rect source = new(image.Size);
        Rect destination = CalculateDestination(natural, Bounds.Size, EffectiveStretch(natural), VerticalBias);
        context.DrawImage(image, source, destination);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
            OnLayoutChanged();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
            _topLevel.ScalingChanged += TopLevel_ScalingChanged;

        // Detaching released the bitmap; a control that comes back needs it again.
        if (_loaded == null && _loadCancellation == null && !string.IsNullOrWhiteSpace(Source))
            BeginLoad();
        else
            CheckResolution();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel != null)
        {
            _topLevel.ScalingChanged -= TopLevel_ScalingChanged;
            _topLevel = null;
        }

        CancelUpgrade();
        CancelLoad();
        ReplaceBitmap(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void TopLevel_ScalingChanged(object? sender, EventArgs e) => OnLayoutChanged();

    /// <summary>
    /// A size or scaling change either lets a waiting load go ahead or, when the picture on
    /// screen is now too small for its box, asks for a sharper one.
    /// </summary>
    private void OnLayoutChanged()
    {
        TaskCompletionSource? waiting = _layoutSignal;
        if (waiting != null)
        {
            _layoutSignal = null;
            waiting.TrySetResult();
        }

        CheckResolution();
    }

    private void CheckResolution()
    {
        if (_loaded is not { IsFullSize: false } loaded || _loadCancellation != null)
            return;

        int? needed = DecodeWidthFor(loaded.SourcePixels);
        // A few pixels of slack, so a box that settles a sub-pixel larger is not a new decode.
        if (needed is { } width && (width == 0 || width > loaded.Bitmap.PixelSize.Width + 4))
            ScheduleUpgrade();
    }

    private void ScheduleUpgrade()
    {
        CancelUpgrade();
        _pendingUpgrade = DispatcherTimer.RunOnce(() =>
        {
            _pendingUpgrade = null;
            if (_loaded != null && _loadCancellation == null)
                BeginLoad(upgrade: true);
        }, UpgradeDelay, DispatcherPriority.Background);
    }

    private void CancelUpgrade()
    {
        _pendingUpgrade?.Dispose();
        _pendingUpgrade = null;
    }

    private void BeginLoad(bool upgrade = false)
    {
        CancelUpgrade();
        CancelLoad();
        long version = Interlocked.Increment(ref _loadVersion);
        string? source = Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            ReplaceBitmap(null);
            return;
        }

        _loadCancellation = new CancellationTokenSource();
        _ = LoadAsync(source, version, upgrade, _loadCancellation.Token);
    }

    private async Task LoadAsync(string source, long version, bool upgrade, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            ImageHeader header = await Task.Run(() => ReadHeaderAsync(source, cancellationToken), cancellationToken);

            // The decode size comes from the arranged box, so it waits for layout. Background
            // priority runs after the layout pass that usually follows a new Source.
            int decodeWidth;
            while (true)
            {
                LayoutAnswer answer = await Dispatcher.UIThread.InvokeAsync(
                    () => AskLayout(header.PixelSize, version, cancellationToken),
                    DispatcherPriority.Background);

                if (answer.Stale)
                    return;

                if (answer.Wait is { } wait)
                {
                    await wait.WaitAsync(cancellationToken);
                    continue;
                }

                decodeWidth = answer.DecodeWidth;
                break;
            }

            bitmap = await Task.Run(() => Decode(source, header.Bytes, decodeWidth), cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _loadVersion && !cancellationToken.IsCancellationRequested)
                {
                    // Layout keeps working in the artwork's own units whatever size it was
                    // decoded at, which is what the full decode reported as its Size.
                    Bitmap decoded = bitmap!;
                    double unit = decoded.PixelSize.Width > 0 ? decoded.Size.Width / decoded.PixelSize.Width : 1;
                    PixelSize pixels = header.PixelSize is { Width: > 0, Height: > 0 } known ? known : decoded.PixelSize;
                    Size natural = new(pixels.Width * unit, pixels.Height * unit);
                    bool fullSize = decoded.PixelSize.Width >= pixels.Width;

                    _loadCancellation?.Dispose();
                    _loadCancellation = null;
                    ReplaceBitmap(new LoadedImage(decoded, natural, pixels, fullSize));
                    bitmap = null;

                    // The box may have grown while this decode was running.
                    CheckResolution();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _loadVersion)
                {
                    _loadCancellation?.Dispose();
                    _loadCancellation = null;

                    // A sharper copy that cannot be had is no reason to drop the one on screen.
                    if (!upgrade)
                        ReplaceBitmap(null);
                }
            });
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private LayoutAnswer AskLayout(PixelSize sourcePixels, long version, CancellationToken cancellationToken)
    {
        if (version != _loadVersion || cancellationToken.IsCancellationRequested)
            return LayoutAnswer.StaleAnswer;

        if (DecodeWidthFor(sourcePixels) is { } width)
            return new LayoutAnswer(false, width, null);

        _layoutSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new LayoutAnswer(false, 0, _layoutSignal.Task);
    }

    /// <summary>
    /// The pixel width worth decoding: 0 for the whole image, or null while layout has not
    /// said how large the image will be drawn.
    /// </summary>
    private int? DecodeWidthFor(PixelSize sourcePixels)
    {
        if (sourcePixels.Width <= 0 || sourcePixels.Height <= 0)
            return 0;

        Size natural = new(sourcePixels.Width, sourcePixels.Height);
        Size box;
        if (IntrinsicSize)
        {
            // A zero side is a box layout has not sized yet, not an unconstrained one; the
            // hero's height is bound to the scroller, which starts at nothing.
            if (_lastAvailableSize is not { Width: > 0, Height: > 0 } available)
                return null;

            box = FitIntrinsic(natural, available);
        }
        else
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
                return null;

            box = Bounds.Size;
        }

        Rect destination = CalculateDestination(natural, box, EffectiveStretch(natural), VerticalBias);
        double drawScale = Math.Max(destination.Width / natural.Width, destination.Height / natural.Height);
        double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double width = Math.Ceiling(sourcePixels.Width * drawScale * renderScaling * HoverHeadroom);

        if (double.IsNaN(width) || double.IsInfinity(width) || width >= sourcePixels.Width)
            return 0;

        return Math.Max(1, (int)width);
    }

    private Stretch EffectiveStretch(Size natural)
        => LetterboxWideArt && natural.Width > natural.Height
            ? Stretch.Uniform
            : Stretch;

    private static Size FitIntrinsic(Size natural, Size availableSize)
    {
        double widthRatio = availableSize.Width / natural.Width;
        double heightRatio = availableSize.Height / natural.Height;
        double scale = Math.Min(widthRatio, heightRatio);

        // Unconstrained in both directions means the natural size is the answer.
        if (double.IsInfinity(scale) || double.IsNaN(scale) || scale <= 0)
            scale = 1;

        return new Size(natural.Width * scale, natural.Height * scale);
    }

    /// <summary>
    /// Reads only the image header, so the decode can be sized before any pixels exist.
    /// Remote images are fetched once here and decoded from the same bytes.
    /// </summary>
    private static async Task<ImageHeader> ReadHeaderAsync(string source, CancellationToken cancellationToken)
    {
        if (TryGetRemoteUri(source, out Uri? uri))
        {
            byte[] bytes = await Http.GetByteArrayAsync(uri, cancellationToken);
            using SKData data = SKData.CreateCopy(bytes);
            using SKCodec? remoteCodec = SKCodec.Create(data);
            return new ImageHeader(ToPixelSize(remoteCodec), bytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using SKCodec? codec = SKCodec.Create(GetLocalPath(source));
        return new ImageHeader(ToPixelSize(codec), null);
    }

    private static PixelSize ToPixelSize(SKCodec? codec)
        => codec == null ? default : new PixelSize(codec.Info.Width, codec.Info.Height);

    private static Bitmap Decode(string source, byte[]? bytes, int decodeWidth)
    {
        using Stream stream = bytes != null
            ? new MemoryStream(bytes, writable: false)
            : new FileStream(GetLocalPath(source), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920);

        return decodeWidth > 0
            ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality)
            : new Bitmap(stream);
    }

    private static bool TryGetRemoteUri(string source, out Uri? uri)
        => Uri.TryCreate(source, UriKind.Absolute, out uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string GetLocalPath(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.IsFile
            ? uri.LocalPath
            : source;

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        TaskCompletionSource? waiting = _layoutSignal;
        _layoutSignal = null;
        waiting?.TrySetCanceled();
    }

    private void ReplaceBitmap(LoadedImage? image)
    {
        LoadedImage? previous = _loaded;
        _loaded = image;
        previous?.Bitmap.Dispose();

        if (IntrinsicSize)
            InvalidateMeasure();

        InvalidateVisual();
    }

    private static Rect CalculateDestination(Size source, Size target, Stretch stretch, double verticalBias)
    {
        if (stretch == Stretch.Fill)
            return new Rect(target);

        double scaleX = target.Width / source.Width;
        double scaleY = target.Height / source.Height;
        double scale = stretch == Stretch.UniformToFill
            ? Math.Max(scaleX, scaleY)
            : Math.Min(scaleX, scaleY);

        if (stretch == Stretch.None)
            scale = 1;

        Size size = new(source.Width * scale, source.Height * scale);
        double bias = double.IsNaN(verticalBias) ? 0.5 : Math.Clamp(verticalBias, 0, 1);
        return new Rect(
            (target.Width - size.Width) / 2,
            (target.Height - size.Height) * bias,
            size.Width,
            size.Height);
    }

    /// <summary>A decoded bitmap together with the size of the artwork it was decoded from.</summary>
    private sealed record LoadedImage(Bitmap Bitmap, Size NaturalSize, PixelSize SourcePixels, bool IsFullSize);

    private readonly record struct ImageHeader(PixelSize PixelSize, byte[]? Bytes);

    private readonly record struct LayoutAnswer(bool Stale, int DecodeWidth, Task? Wait)
    {
        public static LayoutAnswer StaleAnswer => new(true, 0, null);
    }
}
