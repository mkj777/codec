using Codec.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace Codec.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<Game> Games { get; set; } = new();

        public MainViewModel()
        {
            Games.CollectionChanged += Games_CollectionChanged;
        }

        private void Games_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasGames));
            OnPropertyChanged(nameof(IsEmptyLibrary));
            OnPropertyChanged(nameof(IsLibraryVisible));
        }

        public bool HasGames => Games.Count > 0;
        public bool IsEmptyLibrary => !HasGames;

        private bool _isInitialLoading = true;
        public bool IsInitialLoading
        {
            get => _isInitialLoading;
            set => SetProperty(ref _isInitialLoading, value, notifyLibraryVisibility: true);
        }

        private bool _isOnboardingVisible;
        public bool IsOnboardingVisible
        {
            get => _isOnboardingVisible;
            set => SetProperty(ref _isOnboardingVisible, value, notifyLibraryVisibility: true);
        }

        private bool _isLoadingVisible;
        public bool IsLoadingVisible
        {
            get => _isLoadingVisible;
            set => SetProperty(ref _isLoadingVisible, value, notifyLibraryVisibility: true);
        }

        private string _loadingTitle = "Finding your games...";
        public string LoadingTitle
        {
            get => _loadingTitle;
            set => SetProperty(ref _loadingTitle, value);
        }

        private string _loadingSubtitle = "This will take a few minutes, depending on system size.";
        public string LoadingSubtitle
        {
            get => _loadingSubtitle;
            set => SetProperty(ref _loadingSubtitle, value);
        }

        public bool IsLibraryVisible => !IsInitialLoading && !IsOnboardingVisible && !IsLoadingVisible;

        private Game? _selectedGame;
        public Game? SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (_selectedGame != value)
                {
                    _selectedGame = value;
                    OnPropertyChanged();
                }
            }
        }

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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetProperty<T>(ref T storage, T value, bool notifyLibraryVisibility = false, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            if (notifyLibraryVisibility)
            {
                OnPropertyChanged(nameof(IsLibraryVisible));
            }

            return true;
        }
    }
}
