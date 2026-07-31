namespace WinMeters.Services;

internal static class ThemeService
{
    public static void InitializeDarkMode()
    {
        try
        {
            if (NativeMethods.ShouldSystemUseDarkMode() != 0)
            {
                NativeMethods.SetPreferredAppMode(NativeMethods.PREFERRED_APP_MODE_FORCE_DARK);
                NativeMethods.FlushMenuThemes();
            }
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"ThemeService.InitializeDarkMode: {ex.Message}");
        }
    }
}
