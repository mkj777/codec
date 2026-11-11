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
    /// <summary>Not a Velopack install, so there is nothing to check against.</summary>
    DevelopmentMode,
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
            if (!_manager.IsInstalled)
            {
                // A build run from the IDE or a build folder cannot be updated, and that is
                // not a failure worth announcing as one.
                Debug.WriteLine("[UpdateService] Development-mode install detected - skipping check");
                SetStatus(UpdateStatus.DevelopmentMode);
                return;
            }

            SetStatus(UpdateStatus.Checking);

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
