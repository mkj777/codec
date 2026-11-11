using Codec.Helpers;
using Codec.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Threading.Tasks;

namespace Codec.ViewModels;

/// <summary>
/// The games the player hid, and the one way back for each of them. The list lives in
/// Settings, so it only has to answer what is hidden and let it be restored.
/// </summary>
public partial class MainViewModel
{
    public RangeObservableCollection<Game> HiddenGames { get; } = new();

    public int HiddenGameCount => HiddenGames.Count;

    public bool HasHiddenGames => HiddenGames.Count > 0;

    // Per session on purpose: the list opens collapsed every time Codec starts.
    [ObservableProperty]
    private bool _isHiddenGamesExpanded;

    [RelayCommand]
    private void ToggleHiddenGames() => IsHiddenGamesExpanded = !IsHiddenGamesExpanded;

    [RelayCommand]
    private async Task RestoreHiddenGameAsync(Game? game)
    {
        if (game == null || !game.IsHidden)
            return;

        // The game never left the library, so clearing the flag puts it back at its
        // sorted place with its favourite, play data, artwork and launch options intact.
        game.IsHidden = false;
        await _services.LibraryStorage.SaveAsync(Games.ToList());
    }

    private void RefreshHiddenGames()
    {
        var hidden = Games
            .Where(game => game.IsHidden)
            .OrderBy(game => game.Name ?? string.Empty, GameNameComparer)
            .ThenBy(game => game.Id)
            .ToList();

        // Steam sync and scans change Games in bursts; an unchanged list is left alone so the
        // open Settings panel does not rebuild under the pointer.
        if (hidden.SequenceEqual(HiddenGames))
            return;

        HiddenGames.ReplaceAll(hidden);
        OnPropertyChanged(nameof(HiddenGameCount));
        OnPropertyChanged(nameof(HasHiddenGames));

        // Restoring the last one closes the section; the next hide starts it collapsed.
        if (hidden.Count == 0)
            IsHiddenGamesExpanded = false;
    }
}
