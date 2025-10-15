using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Codec.Models
{
    public class Game
    {
        [SetsRequiredMembers]
        public Game()
        {
            Name = string.Empty;
            Executable = string.Empty;
            FolderLocation = string.Empty;
            ImportedFrom = string.Empty;
        }
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

        // game details, fetched from various sources
        public string? Publisher { get; set; }
        public string? Developer { get; set; }
        public List<string>? Genres { get; set; }
        public string? Description { get; set; }
        public List<string>? Platforms { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public double? SteamRating { get; set; }
        public string? AgeRating { get; set; }
        public int? TimeToCompleteMainStory { get; set; } // in seconds
        public int? TimeToCompleteCompletionist { get; set; } // in seconds

        // game assets via local paths. default are placeholder images
        public string LibCapsule { get; set; } = "https://placehold.co/600x900/1c1c1c/ffffff?text=Capsule";
        public string LibHero { get; set; } = "https://placehold.co/1920x620/1c1c1c/ffffff?text=Hero";
        public string LibLogo { get; set; } = "https://placehold.co/1280x260/1c1c1c/ffffff?text=Logo";
        public string LibIcon { get; set; } = "https://placehold.co/32x32/1c1c1c/ffffff?text=Icon";
        public string LibClientIcon { get; set; } = "https://placehold.co/256x256/1c1c1c/ffffff?text=Client+Icon";
        public List<string> Screenshots { get; set; } = new();

        // optional: local path to .bat Script to launch the game
        public string? LaunchScript { get; set; }

        // tracking
        public DateTime? LastUpdated { get; set; }
        public DateTime? LastLaunched { get; set; }
        public double? PlayTime { get; set; } // in seconds
    }
}