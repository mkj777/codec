using Codec.Services.Storage;
using System.Text.Json;
using Xunit;

namespace Codec.Tests
{
    public sealed class AppSettingsTests
    {
        // Startup scanning is a choice onboarding asks for, so until it has been made the
        // setting stays off. The default was on before the choice moved into onboarding.
        [Fact]
        public void NewSettings_LeaveScanOnStartupOffUntilOnboardingChooses()
        {
            var settings = new AppSettings();

            Assert.False(settings.ScanOnStartup);
        }

        [Fact]
        public void Deserialize_MissingScanOnStartup_UsesDefault()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("{}");

            Assert.NotNull(settings);
            Assert.False(settings.ScanOnStartup);
        }

        [Fact]
        public void Deserialize_ExplicitScanOnStartupFalse_PreservesSavedChoice()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"ScanOnStartup":false}""");

            Assert.NotNull(settings);
            Assert.False(settings.ScanOnStartup);
        }

        [Fact]
        public void NewSettings_SelectedSortIndexDefaultIsZero()
        {
            var settings = new AppSettings();

            Assert.Equal(0, settings.SelectedSortIndex);
        }

        [Fact]
        public void Deserialize_ExplicitSelectedSortIndex_PreservesSavedChoice()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"SelectedSortIndex":2}""");

            Assert.NotNull(settings);
            Assert.Equal(2, settings.SelectedSortIndex);
        }

        // Settings written before the counter existed must load as "no failures yet".
        [Fact]
        public void Deserialize_MissingSteamBackgroundSyncFailures_StartsAtZero()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"SteamAccountName":"player"}""");

            Assert.NotNull(settings);
            Assert.Equal(0, settings.SteamBackgroundSyncFailures);
        }

        [Fact]
        public void SteamBackgroundSyncFailures_SurvivesRoundTrip()
        {
            string json = JsonSerializer.Serialize(new AppSettings { SteamBackgroundSyncFailures = 2 });

            var settings = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(settings);
            Assert.Equal(2, settings.SteamBackgroundSyncFailures);
        }
    }
}
