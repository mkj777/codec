namespace Codec.Infrastructure;

public interface IFilePickerService
{
    Task<string?> PickExecutableAsync(string? initialDirectory = null);

    Task<string?> PickLaunchScriptAsync(string? initialDirectory = null);
}
