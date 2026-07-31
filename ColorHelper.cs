using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace WinMeters
{
    public static class ColorHelper
    {
        private static readonly SolidColorBrush _transparentBrush =
            CreateBrush(WpfColor.Transparent);

        private static readonly ConcurrentDictionary<string, SolidColorBrush> _brushCache =
            new(StringComparer.OrdinalIgnoreCase);

        public static SolidColorBrush ParseBrush(
            string color,
            SolidColorBrush? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(color))
                return fallback ?? _transparentBrush;

            if (_brushCache.TryGetValue(color, out var cached))
                return cached;

            try
            {
                if (ColorConverter.ConvertFromString(color) is not WpfColor mediaColor)
                    return fallback ?? _transparentBrush;

                var brush = CreateBrush(mediaColor);

                // GetOrAdd handles races where multiple threads parse
                // the same color at the same time.
                return _brushCache.GetOrAdd(color, brush);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D(
                    $"ColorHelper.ParseBrush: '{color}': {ex.Message}");

                return fallback ?? _transparentBrush;
            }
        }

        public static SolidColorBrush FromDrawingColor(DrawingColor color) =>
            CreateBrush(WpfColor.FromArgb(
                color.A,
                color.R,
                color.G,
                color.B));

        public static DrawingColor ToDrawingColor(string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor))
                return DrawingColor.Transparent;

            try
            {
                if (ColorConverter.ConvertFromString(hexColor) is WpfColor mediaColor)
                {
                    return DrawingColor.FromArgb(
                        mediaColor.A,
                        mediaColor.R,
                        mediaColor.G,
                        mediaColor.B);
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D(
                    $"ColorHelper.ToDrawingColor: '{hexColor}': {ex.Message}");
            }

            return DrawingColor.Transparent;
        }

        public static string ToHexString(WpfColor color) =>
            $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        public static string ToHexString(DrawingColor color) =>
            $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        public static WpfColor? ParseColor(string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor))
                return null;

            try
            {
                return ColorConverter.ConvertFromString(hexColor) is WpfColor color
                    ? color
                    : null;
            }
            catch (Exception ex)
            {
                WinMeters.Log.D(
                    $"ColorHelper.ParseColor: '{hexColor}': {ex.Message}");

                return null;
            }
        }

        public static SolidColorBrush? ThemeBrush(string resourceKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
                return null;

            return Application.Current?.Resources[resourceKey] as SolidColorBrush;
        }

        private static SolidColorBrush CreateBrush(WpfColor color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}