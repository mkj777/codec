using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Codec.Services;

public enum UpdateStatus
{
    Idle,
    Checking,
    Downloading,
    Ready,
    NoUpdateFound,
    Error,
}

public sealed class UpdateService
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public int DownloadProgress { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event Action? StatusChanged;

    private const string GitHubRepo = "mkj777/codec";

    public UpdateService()
    {
        _manager = new UpdateManager(new GithubSource($"https://github.com/{GitHubRepo}", null, false));
    }

    private void SetStatus(UpdateStatus next)
    {
        Status = next;
        Debug.WriteLine($"[UpdateService] Status -> {next}");
        StatusChanged?.Invoke();
    }

    public async Task CheckAndDownloadAsync()
    {
        Debug.WriteLine("[UpdateService] CheckAndDownloadAsync invoked");

        try
        {
            SetStatus(UpdateStatus.Checking);

            if (!_manager.IsInstalled)
            {
                ErrorMessage = "The app seems to be in development mode. If not, please contact the developer on GitHub.";
                Debug.WriteLine("[UpdateService] Development-mode install detected - skipping check");
                await Task.Delay(1500);
                SetStatus(UpdateStatus.Error);
                return;
            }

            // Hold checking visible at least briefly so UI can show it
            var checkTask = _manager.CheckForUpdatesAsync();
            await Task.WhenAll(checkTask, Task.Delay(1500));
            var update = await checkTask;

            if (update is null)
            {
                SetStatus(UpdateStatus.NoUpdateFound);
                return;
            }

            await _manager.DownloadUpdatesAsync(update, pct =>
            {
                DownloadProgress = pct;
                SetStatus(UpdateStatus.Downloading);
            });

            _pendingUpdate = update;
            SetStatus(UpdateStatus.Ready);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Debug.WriteLine($"[UpdateService] ERROR: {ex}");
            SetStatus(UpdateStatus.Error);
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate is null) return;
        _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
