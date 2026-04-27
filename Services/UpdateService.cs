using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Codec.Services;

public sealed class UpdateService
{
    private const string GitHubRepo = "mkj777/codec";

    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;

    public event Action? UpdateReady;

    public UpdateService()
    {
        _manager = new UpdateManager(new GithubSource($"https://github.com/{GitHubRepo}", null, false));
    }

    public async Task CheckAndDownloadAsync()
    {
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null) return;

            await _manager.DownloadUpdatesAsync(update);
            _pendingUpdate = update;
            UpdateReady?.Invoke();
        }
        catch
        {
            // Silent — update errors must not affect the app
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate is null) return;
        _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
