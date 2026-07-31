namespace WinMeters.Services;

internal interface IBarMenuDelegate
{
    void HandleShowSettings();
    void HandleOpenTaskManager();
    void HandleOpenAbout();
    void HandleRestart();
    void HandleExit();
    void HandleToggleLock();
    void HandleToggleSnap();
    void HandleToggleKeepOnTop();
    void HandleToggleHideInFullscreen();
}
