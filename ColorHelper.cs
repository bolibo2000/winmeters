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

        // Forced-dark brushes pairing with the theme-sampling ones above. The user picked
        // "always dark, hardcoded brushes" so the SettingsWindow never falls back to the
        // system theme even on Windows light-mode installations. The four values mirror
        // a typical Windows 10 dark-mode menu: a near-black slate for the background,
        // crisp white for non-selected text, the standard Win10 accent blue for the
        // highlighted-item keyline, and white text on top of that accent. All four are
        // frozen at static-init time so they're safe to bind through a Style setter or
        // assign to a Window DP without dispatcher-thread context or DP-setter checks.
        private static readonly SolidColorBrush _darkMenuBackground =
            Frozen(new SolidColorBrush(WpfColor.FromRgb(0x20, 0x20, 0x20)));
        private static readonly SolidColorBrush _darkMenuText =
            Frozen(new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush _darkHighlight =
            Frozen(new SolidColorBrush(WpfColor.FromRgb(0x00, 0x78, 0xD7)));
        private static readonly SolidColorBrush _darkHighlightText =
            Frozen(new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF)));

        /// <summary>Background brush (#202020). Used as <see cref="System.Windows.Window.Background"/> by the SettingsWindow so the dialog lands on dark slate regardless of the user's OS theme.</summary>
        public static SolidColorBrush DarkMenuBackgroundBrush => _darkMenuBackground;
        /// <summary>Text-foreground brush (#FFFFFF). Mirrors COLOR_MENUTEXT in Win10 dark mode; inherits through the WPF Visual tree from <c>this.Foreground</c>.</summary>
        public static SolidColorBrush DarkMenuTextBrush => _darkMenuText;
        /// <summary>Highlighted-item background brush (#0078D7). Standard Win10 accent blue. Used as the ListBoxItem.IsSelected / IsMouseOver Background so the meter-order entry reads with the look of a highlighted native menu item.</summary>
        public static SolidColorBrush DarkHighlightBrush => _darkHighlight;
        /// <summary>Highlighted-item text-foreground brush (#FFFFFF). Used as the companion Foreground to DarkHighlightBrush on selected / hovered ListBoxItem entries.</summary>
        public static SolidColorBrush DarkHighlightTextBrush => _darkHighlightText;

        // Tiny helper called from field initializers above so we don't repeat the
        // Freeze() dance four times. Frozen brushes are required for cross-thread
        // brush sharing (the SettingsWindow ctor runs on the WPF UI thread but the
        // brushes are also captured inside the IsMouseOver / IsSelected style
        // triggers that WPF may re-applied on background dispatcher work).
        private static SolidColorBrush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

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

        /// <summary>
        /// Returns a SolidColorBrush matching the OS's current menu-background
        /// brush, sampled live from <c>USER32!GetSysColor(COLOR_MENU)</c>. This
        /// is the exact same brush the OS uses to paint the native Win32 HMENU
        /// that the bar's RMB popup displays, so the WPF SettingsWindow dialog
        /// lands on the user's existing chrome without any styling code of its
        /// own. Folds the dark / light theme choice (and any custom accent the
        /// user has configured in Windows Personalization) into a single
        /// read at dialog-open time.
        ///
        /// Returns <c>null</c> (no exception escaped) only if the OS call fails
        /// outright - rare, but covers very old Windows versions where the
        /// GetSysColor ordinal behaves differently. Callers should null-coalesce
        /// on the result so a missing brush falls back to the WPF Window
        /// default background instead of throwing.
        ///
        /// One-shot read: the brush is sampled exactly once at dialog ctor and
        /// cached on the Window.BACKGROUND dependency property. If the user
        /// switcheS the system theme while the dialog is open the menu chrome
        /// differs from the dialog background until the dialog is reopened.
        /// Living with that mismatch is acceptable (settings dialogs are short
        /// lived and the theme toggle mid-edit is an uncommon event); a
        /// WM_SETTINGCHANGE hook could close the gap if it ever becomes a
        /// real complaint.
        /// </summary>
        public static SolidColorBrush? GetMenuBackgroundBrush()
            => GetSysColorBrush(NativeMethods.COLOR_MENU);

        /// <summary>
        /// Foreground of non-selected menu items. Used as
        /// <see cref="System.Windows.Window.Foreground"/> in SettingsWindow
        /// so every text-bearing child (TextBlocks, CheckBox.Content,
        /// Button.Content, ComboBox items, ListBoxItem labels) inherits a
        /// color matching the native HMENU's non-selected item text.
        /// </summary>
        public static SolidColorBrush? GetMenuTextBrush()
            => GetSysColorBrush(NativeMethods.COLOR_MENUTEXT);

        /// <summary>
        /// Background of selected / highlighted menu items. Used as the
        /// ListBoxItem.IsSelected background in SettingsWindow so the
        /// selected entry in the meter-order list reads like a highlighted
        /// native HMENU entry (the same color the OS paints when the user
        /// hovers / keyboard-focuses a menu item).
        /// </summary>
        public static SolidColorBrush? GetHighlightBrush()
            => GetSysColorBrush(NativeMethods.COLOR_HIGHLIGHT);

        /// <summary>
        /// Foreground of selected / highlighted menu items. Companion to
        /// <see cref="GetHighlightBrush"/> - used as the ListBoxItem.IsSelected
        /// foreground so the selected meter text reads with the same
        /// contrast the native HMENU uses for highlighted entries.
        /// </summary>
        public static SolidColorBrush? GetHighlightTextBrush()
            => GetSysColorBrush(NativeMethods.COLOR_HIGHLIGHTTEXT);

        /// <summary>
        /// Shared extraction path: reads <paramref name="sysColorIndex"/>
        /// from USER32!GetSysColor, unpacks COLORREF (0x00BBGGRR) into RGB,
        /// constructs a frozen WPF SolidColorBrush (safe to bind from any
        /// WPF dispatcher context -- no inheritance-context churn, no
        /// thread-affinity trap), and returns it. Logs and returns null on
        /// the rare OS call failure so callers can simply null-coalesce.
        /// Frozen brushes are the canonical way to bind a logically-immutable
        /// color to a DependencyProperty; we Freeze() before returning so
        /// WPF accepts the brush without re-checking IsFrozen on every bind.
        /// </summary>
        private static SolidColorBrush? GetSysColorBrush(int sysColorIndex)
        {
            try
            {
                int colorref = NativeMethods.GetSysColor(sysColorIndex);
                byte r = (byte)(colorref & 0xFF);
                byte g = (byte)((colorref >> 8) & 0xFF);
                byte b = (byte)((colorref >> 16) & 0xFF);
                var brush = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }
            catch (System.Exception ex)
            {
                WinMeters.Log.D($"ColorHelper.GetSysColorBrush({sysColorIndex}): {ex.Message}");
                return null;
            }
        }
    }
}
