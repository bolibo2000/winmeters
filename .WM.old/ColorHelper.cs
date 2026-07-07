using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace WinMeters
{
    /// <summary>
    /// Shared utility class for color parsing operations.
    /// Optimized with cached converter instance and direct WPF Color conversion.
    /// </summary>
    public static class ColorHelper
    {
        private static readonly BrushConverter _brushConverter = new();
        private static readonly SolidColorBrush _transparentBrush = WpfBrushes.Transparent;

        /// <summary>
        /// Parses a hex color string into a SolidColorBrush.
        /// Uses cached BrushConverter instance for improved performance.
        /// </summary>
        /// <param name="color">Hex color string (e.g., "#FF202020" or "#AAAAAA")</param>
        /// <param name="fallback">Optional fallback brush if parsing fails</param>
        /// <returns>Parsed SolidColorBrush or fallback/transparent if parsing fails</returns>
        public static SolidColorBrush ParseBrush(string color, SolidColorBrush? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(color))
                return fallback ?? _transparentBrush;

            try
            {
                if (_brushConverter.ConvertFrom(color) is SolidColorBrush brush)
                {
                    return brush;
                }
            }
            catch (System.Exception ex)
            {
                WinMeters.Log.D($"ColorHelper.ParseBrush: Failed to parse '{color}': {ex.Message}");
            }
            return fallback ?? _transparentBrush;
        }

        /// <summary>
        /// Converts a WPF Color directly to a SolidColorBrush.
        /// More efficient than FromDrawingColor as it avoids System.Drawing dependency.
        /// </summary>
        public static SolidColorBrush FromColor(WpfColor color)
        {
            return new SolidColorBrush(color);
        }

        /// <summary>
        /// Converts a System.Drawing.Color to a WPF SolidColorBrush.
        /// </summary>
        public static SolidColorBrush FromDrawingColor(DrawingColor color)
        {
            return new SolidColorBrush(WpfColor.FromArgb(color.A, color.R, color.G, color.B));
        }

        /// <summary>
        /// Converts a hex string to a System.Drawing.Color for use with Windows Forms ColorDialog.
        /// </summary>
        public static DrawingColor ToDrawingColor(string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor))
                return DrawingColor.Transparent;

            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(hexColor) is WpfColor mediaColor)
                {
                    return DrawingColor.FromArgb(mediaColor.A, mediaColor.R, mediaColor.G, mediaColor.B);
                }
            }
            catch { }
            return DrawingColor.Transparent;
        }

        /// <summary>
        /// Formats a WPF Color as an ARGB hex string.
        /// </summary>
        public static string ToHexString(WpfColor color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// Formats a System.Drawing.Color as an ARGB hex string.
        /// </summary>
        public static string ToHexString(DrawingColor color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// Converts a hex color string directly to a WPF Color.
        /// </summary>
        public static WpfColor? ParseColor(string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor))
                return null;

            try
            {
                return (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
            }
            catch
            {
                return null;
            }
        }
    }
}
