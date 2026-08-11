using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Codec.Infrastructure;

public sealed class AvaloniaFilePickerService(Func<TopLevel?> getTopLevel) : IFilePickerService
{
    private static readonly FilePickerFileType ExecutableFileType = new("Executable")
    {
        Patterns = ["*.exe"]
    };

    private static readonly FilePickerFileType LaunchScriptFileType = new("Launch script")
    {
        Patterns = ["*.bat", "*.cmd", "*.lnk"]
    };

    public Task<string?> PickExecutableAsync(string? initialDirectory = null)
        => PickFileAsync("Select executable", ExecutableFileType, initialDirectory);

    public Task<string?> PickLaunchScriptAsync(string? initialDirectory = null)
        => PickFileAsync("Select launch script", LaunchScriptFileType, initialDirectory);

    private async Task<string?> PickFileAsync(
        string title,
        FilePickerFileType fileType,
        string? initialDirectory)
    {
        IStorageProvider? storageProvider = getTopLevel()?.StorageProvider;
        if (storageProvider == null)
            return null;

        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            startLocation = await storageProvider.TryGetFolderFromPathAsync(initialDirectory);

        IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter = [fileType]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
