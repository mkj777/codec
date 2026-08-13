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

    static CodecImage()
    {
        AffectsRender<CodecImage>(StretchProperty, PlaceholderProperty, VerticalBiasProperty);
        SourceProperty.Changed.AddClassHandler<CodecImage>((control, _) => control.BeginLoad());
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Bitmap? image = _loadedBitmap ?? Placeholder;
        if (image == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        Rect source = new(image.Size);
        Rect destination = CalculateDestination(image.Size, Bounds.Size, Stretch, VerticalBias);
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
