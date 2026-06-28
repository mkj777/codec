using Codec.Services.Storage;
using System.Text.Json;
using Xunit;

namespace Codec.Tests
{
    public sealed class AppSettingsTests
    {
        [Fact]
        public void NewSettings_EnableScanOnStartupByDefault()
        {
            var settings = new AppSettings();

            Assert.True(settings.ScanOnStartup);
        }

        [Fact]
        public void Deserialize_MissingScanOnStartup_UsesDefault()
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("{}");

            Assert.NotNull(settings);
            Assert.True(settings.ScanOnStartup);
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
    }
}
