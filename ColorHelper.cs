using System.Windows.Media;
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
        // Cached brush returned for empty / unparseable input. Avoids allocating a new
        // SolidColorBrush on every miss. Note: we deliberately use an explicit fully-zero
        // ARGB construction rather than `Brushes.Transparent` (= #00FFFFFF) so every
        // channel — alpha AND RGB — is 0. Callers like the PieChart and Border fall back
        // to this via the helper and can rely on "no color anywhere".
        private static readonly SolidColorBrush _transparentBrush =
            new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));

        /// <summary>
        /// Parses a hex color string into a SolidColorBrush.
        /// </summary>
        /// <remarks>
        /// Backed by <see cref="System.Windows.Media.ColorConverter.ConvertFromString"/>, which
        /// accepts the formats used by the WinMeters settings file (8-char ARGB <c>#AARRGGBB</c>,
        /// 6-char RGB <c>#RRGGBB</c>, and named colours). <see cref="BrushConverter"/>
        /// is documented as not thread-safe; <see cref="System.Windows.Media.ColorConverter"/>
        /// is safe to call from any thread, which lets the helper be reused from background
        /// timer ticks without spinlocks.
        /// </remarks>
        /// <param name="color">Hex color string (e.g., "#FF202020" or "#AAAAAA").</param>
        /// <param name="fallback">Optional fallback brush returned when parsing fails.</param>
        /// <returns>The parsed <see cref="SolidColorBrush"/>; <paramref name="fallback"/> or
        /// <see cref="System.Windows.Media.Brushes.Transparent"/> if parsing fails.</returns>
        public static SolidColorBrush ParseBrush(string color, SolidColorBrush? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(color))
                return fallback ?? _transparentBrush;

            try
            {
                var converted = System.Windows.Media.ColorConverter.ConvertFromString(color);
                if (converted is WpfColor mediaColor)
                {
                    return new SolidColorBrush(mediaColor);
                }
            }
            catch (System.Exception ex)
            {
                WinMeters.Log.D($"ColorHelper.ParseBrush: Failed to parse '{color}': {ex.Message}");
            }
            return fallback ?? _transparentBrush;
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
