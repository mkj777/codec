using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrawingColor = System.Drawing.Color;
using UiColor = Windows.UI.Color;

namespace Codec.Helpers
{
    internal sealed record HeroActionPalette(
        UiColor PlayBackgroundColor,
        UiColor SettingsBackgroundColor,
        UiColor PlayBorderStartColor,
        UiColor PlayBorderMidColor,
        UiColor PlayBorderEndColor,
        UiColor SettingsBorderStartColor,
        UiColor SettingsBorderMidColor,
        UiColor SettingsBorderEndColor,
        UiColor ForegroundColor,
        UiColor MutedForegroundColor);

    internal static class HeroActionPaletteExtractor
    {
        private const int SampleMaxDimension = 40;
        private const double DefaultHue = 214d;

        internal static HeroActionPalette Default { get; } = new(
            PlayBackgroundColor: UiColor.FromArgb(0x92, 0x18, 0x21, 0x33),
            SettingsBackgroundColor: UiColor.FromArgb(0x7E, 0x17, 0x1D, 0x29),
            PlayBorderStartColor: UiColor.FromArgb(0xD4, 0xFF, 0xFF, 0xFF),
            PlayBorderMidColor: UiColor.FromArgb(0x6F, 0xA1, 0xC2, 0xE2),
            PlayBorderEndColor: UiColor.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            SettingsBorderStartColor: UiColor.FromArgb(0xA4, 0xFF, 0xFF, 0xFF),
            SettingsBorderMidColor: UiColor.FromArgb(0x48, 0xC5, 0xD5, 0xE6),
            SettingsBorderEndColor: UiColor.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            ForegroundColor: UiColor.FromArgb(0xFF, 0xF3, 0xF6, 0xFB),
            MutedForegroundColor: UiColor.FromArgb(0xFF, 0xDD, 0xE5, 0xEF));

        internal static Task<HeroActionPalette> ExtractAsync(string? heroCachePath, string? heroPath, CancellationToken cancellationToken = default)
            => Task.Run(() => Extract(heroCachePath, heroPath, cancellationToken), cancellationToken);

        internal static HeroActionPalette Extract(string? heroCachePath, string? heroPath)
            => Extract(heroCachePath, heroPath, CancellationToken.None);

        private static HeroActionPalette Extract(string? heroCachePath, string? heroPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? localPath = ResolveLocalPath(heroCachePath, heroPath);
            if (string.IsNullOrWhiteSpace(localPath))
            {
                return Default;
            }

            try
            {
                using var source = new Bitmap(localPath);
                cancellationToken.ThrowIfCancellationRequested();
                using var sample = Downsample(source);
                return CreatePalette(sample, cancellationToken);
            }
            catch
            {
                return Default;
            }
        }

        private static string? ResolveLocalPath(string? heroCachePath, string? heroPath)
            => TryResolveExistingFile(heroCachePath) ?? TryResolveExistingFile(heroPath);

        private static string? TryResolveExistingFile(string? pathOrUri)
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
            {
                return null;
            }

            try
            {
                if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out var absoluteUri))
                {
                    return absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath)
                        ? absoluteUri.LocalPath
                        : null;
                }

                return File.Exists(pathOrUri)
                    ? Path.GetFullPath(pathOrUri)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap Downsample(Bitmap source)
        {
            double widthScale = SampleMaxDimension / (double)Math.Max(source.Width, 1);
            double heightScale = SampleMaxDimension / (double)Math.Max(source.Height, 1);
            double scale = Math.Min(1d, Math.Min(widthScale, heightScale));

            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            var sample = new Bitmap(width, height);

            using var graphics = Graphics.FromImage(sample);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));

            return sample;
        }

        private static HeroActionPalette CreatePalette(Bitmap bitmap, CancellationToken cancellationToken)
        {
            double luminanceSum = 0d;
            double saturationWeightSum = 0d;
            double hueVectorX = 0d;
            double hueVectorY = 0d;
            double saturationSum = 0d;
            int sampledPixels = 0;

            for (int y = 0; y < bitmap.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int x = 0; x < bitmap.Width; x++)
                {
                    DrawingColor pixel = bitmap.GetPixel(x, y);
                    if (pixel.A < 12)
                    {
                        continue;
                    }

                    sampledPixels++;
                    double r = pixel.R / 255d;
                    double g = pixel.G / 255d;
                    double b = pixel.B / 255d;
                    double luminance = (0.2126d * r) + (0.7152d * g) + (0.0722d * b);
                    double saturation = pixel.GetSaturation();
                    double hueRadians = pixel.GetHue() * (Math.PI / 180d);
                    double hueWeight = 0.2d + (saturation * 0.8d);

                    luminanceSum += luminance;
                    saturationSum += saturation * hueWeight;
                    saturationWeightSum += hueWeight;
                    hueVectorX += Math.Cos(hueRadians) * hueWeight;
                    hueVectorY += Math.Sin(hueRadians) * hueWeight;
                }
            }

            if (sampledPixels == 0)
            {
                return Default;
            }

            double averageLuminance = luminanceSum / sampledPixels;
            double averageSaturation = saturationWeightSum > 0d
                ? saturationSum / saturationWeightSum
                : 0d;
            double dominantHue = Math.Abs(hueVectorX) + Math.Abs(hueVectorY) > 0.0001d
                ? NormalizeHue(Math.Atan2(hueVectorY, hueVectorX) * (180d / Math.PI))
                : DefaultHue;

            double accentSaturation = Clamp((averageSaturation * 0.72d) + 0.08d, 0.10d, 0.34d);
            double playFillSaturation = Clamp(accentSaturation * 0.75d, 0.10d, 0.24d);
            double settingsFillSaturation = Clamp(playFillSaturation * 0.42d, 0.04d, 0.12d);
            double playLightness = Clamp(0.16d - (averageLuminance * 0.035d), 0.11d, 0.16d);
            double settingsLightness = Clamp(playLightness - 0.018d, 0.09d, 0.135d);

            byte playAlpha = ClampByte(144 + (averageLuminance * 28d));
            byte settingsAlpha = ClampByte(122 + (averageLuminance * 20d));

            UiColor playBackground = FromHsl(dominantHue, playFillSaturation, playLightness, playAlpha);
            UiColor settingsBackground = FromHsl(dominantHue, settingsFillSaturation, settingsLightness, settingsAlpha);
            UiColor playBorderMid = FromHsl(dominantHue, Clamp(accentSaturation * 1.15d, 0.16d, 0.42d), 0.76d, ClampByte(94 + (averageSaturation * 52d)));
            UiColor settingsBorderMid = FromHsl(dominantHue, Clamp(settingsFillSaturation * 1.1d, 0.06d, 0.18d), 0.79d, ClampByte(62 + (averageSaturation * 40d)));

            return new HeroActionPalette(
                PlayBackgroundColor: playBackground,
                SettingsBackgroundColor: settingsBackground,
                PlayBorderStartColor: UiColor.FromArgb(0xD8, 0xFF, 0xFF, 0xFF),
                PlayBorderMidColor: playBorderMid,
                PlayBorderEndColor: UiColor.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
                SettingsBorderStartColor: UiColor.FromArgb(0xA8, 0xFF, 0xFF, 0xFF),
                SettingsBorderMidColor: settingsBorderMid,
                SettingsBorderEndColor: UiColor.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                ForegroundColor: UiColor.FromArgb(0xFF, 0xF3, 0xF6, 0xFB),
                MutedForegroundColor: UiColor.FromArgb(0xFF, 0xDD, 0xE5, 0xEF));
        }

        private static UiColor FromHsl(double hueDegrees, double saturation, double lightness, byte alpha)
        {
            double normalizedHue = NormalizeHue(hueDegrees) / 360d;
            double q = lightness < 0.5d
                ? lightness * (1d + saturation)
                : lightness + saturation - (lightness * saturation);
            double p = (2d * lightness) - q;

            byte r = ToByte(HueToRgb(p, q, normalizedHue + (1d / 3d)));
            byte g = ToByte(HueToRgb(p, q, normalizedHue));
            byte b = ToByte(HueToRgb(p, q, normalizedHue - (1d / 3d)));

            return UiColor.FromArgb(alpha, r, g, b);
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0d)
            {
                t += 1d;
            }

            if (t > 1d)
            {
                t -= 1d;
            }

            if (t < 1d / 6d)
            {
                return p + ((q - p) * 6d * t);
            }

            if (t < 0.5d)
            {
                return q;
            }

            if (t < 2d / 3d)
            {
                return p + ((q - p) * ((2d / 3d) - t) * 6d);
            }

            return p;
        }

        private static byte ToByte(double value)
            => ClampByte(value * 255d);

        private static byte ClampByte(double value)
            => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

        private static double Clamp(double value, double min, double max)
            => Math.Clamp(value, min, max);

        private static double NormalizeHue(double hueDegrees)
        {
            double normalizedHue = hueDegrees % 360d;
            return normalizedHue < 0d ? normalizedHue + 360d : normalizedHue;
        }
    }
}
