using Codec.Models;
using Codec.Services.Scanning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Codec.ViewModels
{
    public partial class MainViewModel
    {
        private readonly SemaphoreSlim _heuristicInstallCheckGate = new(1, 1);

        private async Task RefreshHeuristicInstallStatesAsync()
        {
            if (!await _heuristicInstallCheckGate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                // The folder walks run off the UI thread, so they read copies of the bound games.
                var heuristicGames = await RunOnUiThreadAsync(() => Games
                    .Where(game => game.IsHeuristicScan)
                    .Select(game => (Target: game, Snapshot: game.CreateHydrationSnapshot()))
                    .ToList()).ConfigureAwait(false);
                var updates = new List<(Game Game, HeuristicInstallState State)>();

                foreach ((Game target, Game snapshot) in heuristicGames)
                {
                    HeuristicInstallState? state = await _services.HeuristicInstallState.EvaluateAsync(snapshot).ConfigureAwait(false);
                    if (state.HasValue &&
                        (snapshot.IsInstalled != state.Value.IsInstalled ||
                         state.Value.InstalledSize.HasValue && snapshot.FolderSize != state.Value.InstalledSize.Value))
                    {
                        updates.Add((target, state.Value));
                    }
                }

                if (updates.Count == 0)
                    return;

                List<Game> library = await RunOnUiThreadAsync(() =>
                {
                    foreach ((Game game, HeuristicInstallState state) in updates)
                    {
                        game.IsInstalled = state.IsInstalled;
                        if (state.InstalledSize.HasValue)
                            game.FolderSize = state.InstalledSize.Value;
                    }

                    RefreshSidebarFilteredGames();
                    RefreshDisplayedGames();
                    return Games.ToList();
                }).ConfigureAwait(false);

                await _services.LibraryStorage.SaveAsync(library).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _heuristicInstallCheckGate.Release();
            }
        }
    }
}
