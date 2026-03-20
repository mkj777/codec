using Codec.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Codec.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public ObservableCollection<Game> Games { get; set; } = new();

        public MainViewModel()
        {
            Games.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasGames));
                OnPropertyChanged(nameof(IsEmptyLibrary));
                OnPropertyChanged(nameof(IsLibraryVisible));
            };
        }

        public bool HasGames => Games.Count > 0;
        public bool IsEmptyLibrary => !HasGames;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
        private bool _isInitialLoading = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
        private bool _isOnboardingVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLibraryVisible))]
        private bool _isLoadingVisible;

        [ObservableProperty]
        private string _loadingTitle = "Finding your games...";

        [ObservableProperty]
        private string _loadingSubtitle = "This will take a few minutes...";

        [ObservableProperty]
        private Game? _selectedGame;

        public bool IsLibraryVisible => !IsInitialLoading && !IsOnboardingVisible && !IsLoadingVisible;

        public void SetLoadingState(bool isVisible, string? title = null, string? subtitle = null)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                LoadingTitle = title;
            }

            if (subtitle != null)
            {
                LoadingSubtitle = subtitle;
            }

            IsLoadingVisible = isVisible;
        }
    }
}
