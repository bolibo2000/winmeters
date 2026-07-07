using System;

namespace WinMeters.Services;

/// <summary>
/// Shared helpers for opting the WinMeters process into uxtheme
/// dark-mode aware behavior. uxtheme ordinal #138
/// (ShouldSystemUseDarkMode) is available since Windows 10 1903;
/// calling SetPreferredAppMode before USER32!GetSysColor makes the
/// OS return the dark-themed COLOR_MENU / COLOR_MENUTEXT /
/// COLOR_HIGHLIGHT / COLOR_HIGHLIGHTTEXT values to the process instead
/// of the legacy light-mode values that GetSysColor returns
/// unconditionally on a process that hasn't opted in.
/// </summary>
/// <remarks>
/// Idempotent: re-calling with the same outcome (system is in dark
/// mode -&gt; FORCE_DARK set) is a no-op. Safe to call from multiple
/// cold-init paths (MainWindow ctor + SettingsWindow ctor) because
/// the process-wide PreferredAppMode state is the same throughout
/// the lifetime of WinMeters regardless of which call site flipped
/// it first. The SettingsWindow's ctor and the MainWindow's
/// OnSourceInitialized both call this on their respective cold-open
/// paths so future syscolor-derived brushes anywhere in the app read
/// correctly on the very first paint.
///
/// Older Windows (pre-1903 without uxtheme #138) raise
/// EntryPointNotFoundException from ShouldSystemUseDarkMode which
/// the try/catch absorbs -- those systems fall through to whatever
/// GetSysColor returns without our intervention, which matches the
/// pre-extraction SettingsWindow behavior (acceptable legacy).
/// </remarks>
internal static class ThemeService
{
    /// <summary>
    /// Opt THIS PROCESS into uxtheme-aware dark mode if the user's
    /// Windows installation reports itself as dark-mode. Sets
    /// <c>SetPreferredAppMode(FORCE_DARK) + FlushMenuThemes()</c> so
    /// subsequent <see cref="NativeMethods.GetSysColor"/> calls in
    /// this process return dark-themed brush values. The bar's popup
    /// <c>ApplyMenuChromeMode</c> re-applies the right value before
    /// every <c>TrackPopupMenuEx</c> anyway, so callers don't need to
    /// re-call this per frame or per popup -- once at process start is
    /// enough for cold-opened windows / dialogs to read correct
    /// syscolors.
    /// </summary>
    public static void InitializeDarkMode()
    {
        try
        {
            if (NativeMethods.ShouldSystemUseDarkMode() != 0)
            {
                NativeMethods.SetPreferredAppMode(
                    NativeMethods.PREFERRED_APP_MODE_FORCE_DARK);
                NativeMethods.FlushMenuThemes();
            }
        }
        catch (System.Exception ex)
        {
            WinMeters.Log.D($"ThemeService.InitializeDarkMode: {ex.Message}");
        }
    }
}
