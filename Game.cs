using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Codec
{
    public class Game : INotifyPropertyChanged
    {
        private DateTime? _lastPlayed;

        // the display name of the game
        public string Name { get; set; }

        // the full path to the executable
        public string Executable { get; set; }

        // path to the Cover, can be a URL or local path
        public string Cover { get; set; }

        // optional: Path to .bat- or .ps1 Script to launch the game
        public string? LaunchScript { get; set; }

        // to save when the game was last played
        public DateTime? LastPlayed
        {
            get => _lastPlayed;
            set
            {
                _lastPlayed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPlayedText));
                OnPropertyChanged(nameof(HasBeenPlayed));
            }
        }

        // Computed property for display text
        public string LastPlayedText => LastPlayed.HasValue
            ? $"{LastPlayed.Value:MMM dd, yyyy}"
            : string.Empty;

        // Computed property for visibility
        public bool HasBeenPlayed => LastPlayed.HasValue;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}