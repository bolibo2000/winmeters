namespace WinMeters.Services;

/// <summary>
/// Menu-command dispatch contract used by <see cref="BarPopupMenuService"/>.
/// The service invokes these methods when the user picks an item from the
/// right-click RMB context. <see cref="MainWindow"/> implements this interface
/// so the popup-menu service stays free of WPF shell concerns
/// (<c>OpenSettings</c> / <c>OpenAboutWindow</c> / <c>RestartWinMeters</c> /
/// the four <c>Toggle*</c> helpers) and is testable in isolation.
/// <para>
/// All methods are parameterless; the menu command IDs (1001-1010) encode
/// which one to invoke. All methods run on the UI thread -- the
/// <see cref="BarPopupMenuService.WmRButtonUp"/> HwndSource hook fires on
/// the UI thread so no <c>Dispatcher.Invoke</c> wrapping is needed.
/// </para>
/// </summary>
internal interface IBarMenuDelegate
{
    /// <summary>Cmd 1001 -- open the Settings dialog (existing SettingsWindow).</summary>
    void HandleShowSettings();

    /// <summary>Cmd 1002 -- launch taskmgr.exe via the shell.</summary>
    void HandleOpenTaskManager();

    /// <summary>Cmd 1003 -- open the AboutWindow single-instance dialog.</summary>
    void HandleOpenAbout();

    /// <summary>Cmd 1010 (WinMeters extension beyond the kil0bit 1001-1009 ID space) -- restart WinMeters process.</summary>
    void HandleRestart();

    /// <summary>Cmd 1004 -- exit the application (SavePosition + Application.Current.Shutdown).</summary>
    void HandleExit();

    /// <summary>Cmd 1006 -- toggle <c>_settings.Window.LockPosition</c> + SavePosition.</summary>
    void HandleToggleLock();

    /// <summary>Cmd 1007 -- toggle <c>_settings.Window.StickToTaskbar</c> + ApplyWindowMode + Save.</summary>
    void HandleToggleSnap();

    /// <summary>Cmd 1008 -- toggle <c>_settings.General.KeepOnTop</c> + ApplyKeepOnTop + Save.</summary>
    void HandleToggleKeepOnTop();

    /// <summary>Cmd 1009 -- toggle <c>_settings.General.HideInFullscreen</c> + Save.</summary>
    void HandleToggleHideInFullscreen();
}
