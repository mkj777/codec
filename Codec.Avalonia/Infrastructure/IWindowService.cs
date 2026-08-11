namespace Codec.Infrastructure;

public interface IWindowService
{
    void HideMainWindow();

    void ShowMainWindow();

    void CloseApplication();

    Task OpenExternalUriAsync(Uri uri);
}
