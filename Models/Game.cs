using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Codec.Models
{
    public class Game : INotifyPropertyChanged
    {
        [SetsRequiredMembers]
        public Game()
        {
            Name = string.Empty;
            Executable = string.Empty;
            FolderLocation = string.Empty;
            ImportedFrom = string.Empty;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // basic information
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime DateAdded { get; set; } = DateTime.Now;
        public required string Executable { get; set; }
        public required string FolderLocation { get; set; }
        public required string Name { get; set; }
        public required string ImportedFrom { get; set; } // e.g., "Steam (C:\...\Steam)", "Manual", "Scanned", etc.
        public long FolderSize { get; set; } // doesnt need required, will be 0 if not calculated yet

        // Display-only property that shows just the platform name without the path
        public string ImportedFromDisplay
        {
            get
            {
                if (ImportedFrom.StartsWith("Steam", StringComparison.OrdinalIgnoreCase))
                {
                    return "Steam";
                }
                return ImportedFrom;
            }
        }

        // external IDs for fetching metadata from online databases
        public int? SteamID { get; set; }
        public int? RawgID { get; set; }
        public string? RawgSlug { get; set; }
        public int? GridDbId { get; set; }

        // game details, fetched from various sources
        public string? Publisher { get; set; }
        public string? Developer { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Categories { get; set; }
        public string? Price { get; set; }
        public string? Description { get; set; }
        public List<string>? Platforms { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public double? SteamRating { get; set; }
        public string? SteamReviewSummary { get; set; }
        public int? SteamReviewTotal { get; set; }
        public string? AgeRating { get; set; }
        public int? TimeToCompleteMainStory { get; set; } // in seconds
        public int? TimeToCompleteCompletionist { get; set; } // in seconds

        // game assets via local paths. default are placeholder images
        private string _libCapsule = "https://placehold.co/600x900/1c1c1c/ffffff?text=Capsule";
        public string LibCapsule
        {
            get => _libCapsule;
            set => SetProperty(ref _libCapsule, value);
        }

        private string _libHero = "https://placehold.co/1920x620/1c1c1c/ffffff?text=Hero";
        public string LibHero
        {
            get => _libHero;
            set => SetProperty(ref _libHero, value);
        }

        private string _libLogo = "https://placehold.co/1280x260/1c1c1c/ffffff?text=Logo";
        public string LibLogo
        {
            get => _libLogo;
            set => SetProperty(ref _libLogo, value);
        }

        private string _libIcon = "https://placehold.co/32x32/1c1c1c/ffffff?text=Icon";
        public string LibIcon
        {
            get => _libIcon;
            set => SetProperty(ref _libIcon, value);
        }

        private string _libClientIcon = "https://placehold.co/256x256/1c1c1c/ffffff?text=Client+Icon";
        public string LibClientIcon
        {
            get => _libClientIcon;
            set => SetProperty(ref _libClientIcon, value);
        }
        public List<string> Media { get; set; } = new()
        {
            "https://placehold.co/1920x1080/1c1c1c/ffffff?text=Media+1",
            "https://placehold.co/1920x1080/1c1c1c/ffffff?text=Media+2",
            "https://placehold.co/1920x1080/1c1c1c/ffffff?text=Media+3"
        };

        // optional: local path to .bat Script to launch the game
        public string? LaunchScript { get; set; }

        // tracking
        public DateTime? LastUpdated { get; set; }
        public DateTime? LastLaunched { get; set; }
        public double? PlayTime { get; set; } // in seconds

        // external links
        public string? OfficialWebsiteUrl { get; set; }
        public string? SteamPageUrl { get; set; }
        public string? RawgUrl { get; set; } = "https://rawg.io";
        public string? HltbUrl { get; set; } = "https://howlongtobeat.com";

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        public void NotifyPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}