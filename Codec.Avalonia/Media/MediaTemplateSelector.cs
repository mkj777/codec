using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using Codec.Converters;

namespace Codec.Media;

/// <summary>
/// Picks the image or the video template for one media item.
///
/// One template holding both, with the other half hidden, meant every screenshot in the
/// overlay also carried a video surface: a libvlc instance, a player and a native child
/// window each, built and torn down on every step. A screenshot gets an image and nothing
/// else.
/// </summary>
public sealed class MediaTemplateSelector : IDataTemplate
{
    public const string ImageKey = "image";
    public const string VideoKey = "video";

    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new();

    public Control? Build(object? param)
        => Templates[MediaTypeConverter.IsVideo(param as string) ? VideoKey : ImageKey].Build(param);

    public bool Match(object? data) => data is string;
}
