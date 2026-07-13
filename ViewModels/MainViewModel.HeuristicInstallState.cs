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
                List<Game> heuristicGames = await RunOnUiThreadAsync(() =>
                    Games.Where(game => game.IsHeuristicScan).ToList()).ConfigureAwait(false);
                var updates = new List<(Game Game, HeuristicInstallState State)>();

                foreach (Game game in heuristicGames)
                {
                    HeuristicInstallState? state = await _services.HeuristicInstallState.EvaluateAsync(game).ConfigureAwait(false);
                    if (state.HasValue &&
                        (game.IsInstalled != state.Value.IsInstalled ||
                         state.Value.InstalledSize.HasValue && game.FolderSize != state.Value.InstalledSize.Value))
                    {
                        updates.Add((game, state.Value));
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
