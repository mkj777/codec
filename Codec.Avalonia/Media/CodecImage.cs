using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Codec.Media;

public sealed class CodecImage : Control
{
    private static readonly HttpClient Http = new();
    private CancellationTokenSource? _loadCancellation;
    private Bitmap? _loadedBitmap;
    private long _loadVersion;

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

    static CodecImage()
    {
        AffectsRender<CodecImage>(
            StretchProperty, PlaceholderProperty, VerticalBiasProperty, LetterboxWideArtProperty);
        SourceProperty.Changed.AddClassHandler<CodecImage>((control, _) => control.BeginLoad());
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!IntrinsicSize)
            return base.MeasureOverride(availableSize);

        Bitmap? image = _loadedBitmap ?? Placeholder;
        if (image is null || image.Size.Width <= 0 || image.Size.Height <= 0)
            return default;

        double widthRatio = availableSize.Width / image.Size.Width;
        double heightRatio = availableSize.Height / image.Size.Height;
        double scale = Math.Min(widthRatio, heightRatio);

        // Unconstrained in both directions means the natural size is the answer.
        if (double.IsInfinity(scale) || double.IsNaN(scale) || scale <= 0)
            scale = 1;

        return new Size(image.Size.Width * scale, image.Size.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Bitmap? image = _loadedBitmap ?? Placeholder;
        if (image == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        Rect source = new(image.Size);
        Stretch stretch = LetterboxWideArt && image.Size.Width > image.Size.Height
            ? Stretch.Uniform
            : Stretch;
        Rect destination = CalculateDestination(image.Size, Bounds.Size, stretch, VerticalBias);
        context.DrawImage(image, source, destination);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        CancelLoad();
        ReplaceBitmap(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void BeginLoad()
    {
        CancelLoad();
        long version = Interlocked.Increment(ref _loadVersion);
        string? source = Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            ReplaceBitmap(null);
            return;
        }

        _loadCancellation = new CancellationTokenSource();
        _ = LoadAsync(source, version, _loadCancellation.Token);
    }

    private async Task LoadAsync(string source, long version, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(async () =>
            {
                await using Stream stream = await OpenStreamAsync(source, cancellationToken);
                return new Bitmap(stream);
            }, cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _loadVersion && !cancellationToken.IsCancellationRequested)
                {
                    ReplaceBitmap(bitmap);
                    bitmap = null;
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
                    ReplaceBitmap(null);
            });
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static async Task<Stream> OpenStreamAsync(string source, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            byte[] bytes = await Http.GetByteArrayAsync(uri, cancellationToken);
            return new MemoryStream(bytes, writable: false);
        }

        string path = Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.IsFile
            ? uri.LocalPath
            : source;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void ReplaceBitmap(Bitmap? bitmap)
    {
        Bitmap? previous = _loadedBitmap;
        _loadedBitmap = bitmap;
        previous?.Dispose();

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
}
