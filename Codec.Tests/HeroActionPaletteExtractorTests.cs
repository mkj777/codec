using Codec.Helpers;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Xunit;
using DrawingColor = System.Drawing.Color;
using UiColor = Windows.UI.Color;

namespace Codec.Tests
{
    public sealed class HeroActionPaletteExtractorTests
    {
        [Fact]
        public void Extract_BrightHeroStillProducesDarkPlayBackground()
        {
            string imagePath = CreateSolidImage(DrawingColor.FromArgb(255, 244, 244, 244));

            try
            {
                HeroActionPalette palette = HeroActionPaletteExtractor.Extract(imagePath, null);

                Assert.InRange(GetRelativeLuminance(palette.PlayBackgroundColor), 0d, 0.22d);
                Assert.InRange(GetRelativeLuminance(palette.SettingsBackgroundColor), 0d, 0.18d);
            }
            finally
            {
                DeleteTempImage(imagePath);
            }
        }

        [Fact]
        public void Extract_DarkHeroKeepsReadableForeground()
        {
            string imagePath = CreateSolidImage(DrawingColor.FromArgb(255, 8, 12, 18));

            try
            {
                HeroActionPalette palette = HeroActionPaletteExtractor.Extract(imagePath, null);

                Assert.InRange(GetRelativeLuminance(palette.ForegroundColor), 0.88d, 1d);
                Assert.InRange(GetRelativeLuminance(palette.MutedForegroundColor), 0.75d, 1d);
            }
            finally
            {
                DeleteTempImage(imagePath);
            }
        }

        [Fact]
        public void Extract_MissingHeroReturnsFallbackPalette()
        {
            HeroActionPalette palette = HeroActionPaletteExtractor.Extract(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"),
                null);

            Assert.Equal(HeroActionPaletteExtractor.Default, palette);
        }

        private static string CreateSolidImage(DrawingColor color)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"codec-palette-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "hero.png");

            using var bitmap = new Bitmap(48, 48);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(color);
            bitmap.Save(path, ImageFormat.Png);

            return path;
        }

        private static void DeleteTempImage(string path)
        {
            string? directory = Path.GetDirectoryName(path);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static double GetRelativeLuminance(UiColor color)
        {
            static double Convert(byte channel)
            {
                double normalized = channel / 255d;
                return normalized <= 0.03928d
                    ? normalized / 12.92d
                    : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
            }

            return (0.2126d * Convert(color.R))
                 + (0.7152d * Convert(color.G))
                 + (0.0722d * Convert(color.B));
        }
    }
}
