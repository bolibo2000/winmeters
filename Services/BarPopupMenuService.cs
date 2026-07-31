using System.Runtime.InteropServices;

namespace WinMeters.Services;

internal sealed class BarPopupMenuService
{
    private const int WM_RBUTTONUP = 0x0205;

    private static readonly uint MonitorInfoCbSize =
        (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>();

    private readonly IntPtr _hwnd;
    private readonly IBarMenuDelegate _menuDelegate;
    private AppSettings _settings;

    public BarPopupMenuService(IntPtr hwnd, AppSettings settings, IBarMenuDelegate menuDelegate)
    {
        _hwnd = hwnd;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _menuDelegate = menuDelegate ?? throw new ArgumentNullException(nameof(menuDelegate));
    }

    public void BindSettings(AppSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public IntPtr WmRButtonUp(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_RBUTTONUP) return IntPtr.Zero;
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return IntPtr.Zero;

        ApplyMenuChromeMode(hwnd);

        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            BuildPopupMenu(hMenu);
            NativeMethods.SetForegroundWindow(hwnd);

            int my;
            uint alignFlag;
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT wr) != 0)
            {
                IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO { cbSize = MonitorInfoCbSize };
                NativeMethods.GetMonitorInfo(hMon, ref mi);

                if (wr.Top > (mi.rcWork.Top + mi.rcWork.Bottom) / 2)
                {
                    my = wr.Top - 4;
                    alignFlag = NativeMethods.TPM_BOTTOMALIGN;
                }
                else
                {
                    my = wr.Bottom + 4;
                    alignFlag = NativeMethods.TPM_TOPALIGN;
                }
            }
            else
            {
                my = cursor.Y;
                alignFlag = NativeMethods.TPM_TOPALIGN;
            }

            int ch = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RIGHTALIGN | alignFlag,
                cursor.X, my, hwnd, IntPtr.Zero);

            if (ch != 0) DispatchMenuCommand((uint)ch);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }

        handled = true;
        return IntPtr.Zero;
    }

    private void BuildPopupMenu(IntPtr hMenu)
    {
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_SETTINGS, "Settings");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_TASKMGR, "Task Manager");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);

        NativeMethods.AppendMenu(hMenu, _settings.General.KeepOnTop ? NativeMethods.MF_CHECKED : 0, NativeMethods.IDM_KEEPONTOP, "Keep on Top");
        NativeMethods.AppendMenu(hMenu, _settings.General.HideInFullscreen ? NativeMethods.MF_CHECKED : 0, NativeMethods.IDM_HIDEFULLSCREEN, "Hide in Fullscreen");
        NativeMethods.AppendMenu(hMenu, _settings.Window.LockPosition ? NativeMethods.MF_CHECKED : 0, NativeMethods.IDM_LOCK, "Lock Position");
        NativeMethods.AppendMenu(hMenu, _settings.Window.StickToTaskbar ? NativeMethods.MF_CHECKED : 0, NativeMethods.IDM_SNAP, "Snap to Taskbar");

        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_ABOUT, "About");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_RESTART, "Restart");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_EXIT, "Exit");
    }

    private void DispatchMenuCommand(uint cmd)
    {
        switch (cmd)
        {
            case NativeMethods.IDM_SETTINGS: _menuDelegate.HandleShowSettings(); break;
            case NativeMethods.IDM_TASKMGR: _menuDelegate.HandleOpenTaskManager(); break;
            case NativeMethods.IDM_ABOUT: _menuDelegate.HandleOpenAbout(); break;
            case NativeMethods.IDM_EXIT: _menuDelegate.HandleExit(); break;
            case NativeMethods.IDM_LOCK: _menuDelegate.HandleToggleLock(); break;
            case NativeMethods.IDM_SNAP: _menuDelegate.HandleToggleSnap(); break;
            case NativeMethods.IDM_KEEPONTOP: _menuDelegate.HandleToggleKeepOnTop(); break;
            case NativeMethods.IDM_HIDEFULLSCREEN: _menuDelegate.HandleToggleHideInFullscreen(); break;
            case NativeMethods.IDM_RESTART: _menuDelegate.HandleRestart(); break;
            default: WinMeters.Log.D($"BarPopupMenuService: unknown cmd {cmd}"); break;
        }
    }

    public static void LaunchTaskManager()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskmgr",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { WinMeters.Log.D($"LaunchTaskManager: {ex.Message}"); }
    }

    private static void ApplyMenuChromeMode(IntPtr hwnd)
    {
        try
        {
            bool dark = NativeMethods.ShouldSystemUseDarkMode() != 0;
            NativeMethods.SetPreferredAppMode(dark
                ? NativeMethods.PREFERRED_APP_MODE_FORCE_DARK
                : NativeMethods.PREFERRED_APP_MODE_DEFAULT);
            NativeMethods.AllowDarkModeForWindow(hwnd, dark);
            NativeMethods.FlushMenuThemes();
        }
        catch (System.EntryPointNotFoundException)
        {
            NativeMethods.AllowDarkModeForWindow(hwnd, true);
            NativeMethods.FlushMenuThemes();
        }
    }
}
