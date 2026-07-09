using System.Runtime.InteropServices;

namespace WinMeters.Services;

/// <summary>
/// Native Win32 popup menu for the WinMeters bar's right-click RMB context.
/// Mirrors <c>.Kilobit/OverlayWindow.cs</c> WndProc verbatim: 10 items + 3
/// separators, command IDs 1001-1009 in the same order, <c>MF_CHECKED</c>
/// for the four live toggles, <c>MF_SEPARATOR</c> for the dividers, and
/// the menu chrome forced dark via uxtheme calls (SetPreferredAppMode /
/// AllowDarkModeForWindow / FlushMenuThemes) right before TrackPopupMenuEx.
/// Replaces the previous WPF ContextMenu in <c>MainWindow.xaml</c> (and
/// its 4 WPF MenuItem Click handlers) with a single OS-drawn HMENU.
/// <para>
/// Constructed in <c>MainWindow.OnSourceInitialized</c> after the window's
/// HWND is known, then hooked into the HwndSource via
/// <see cref="WmRButtonUp"/>.
/// </para>
/// <para>
/// Settings reads happen via the bound <c>_settings</c> field --
/// <c>MainWindow</c> calls <see cref="BindSettings"/> whenever the user
/// re-saves from the Settings dialog so the popup always reflects current
/// state (the four MF_CHECKED live toggles).
/// </para>
/// <para>
/// Menu command dispatch is delegated back to MainWindow via the injected
/// <see cref="IBarMenuDelegate"/>, so this class is free of WPF MainWindow
/// shell concerns (OpenSettings / OpenAboutWindow / RestartWinMeters).
/// </para>
/// </summary>
internal sealed class BarPopupMenuService
{
    /// <summary>Win32 WM_RBUTTONUP message id -- fires when the user releases the right mouse button.</summary>
    private const int WM_RBUTTONUP = 0x0205;

    /// <summary>
    /// Cached <see cref="NativeMethods.MONITORINFO.cbSize"/> value. The struct is
    /// fixed-size (40 bytes on x64), so we evaluate Marshal.SizeOf once at type
    /// init instead of on every WM_RBUTTONUP. The shell reads cbSize on entry to
    /// <see cref="NativeMethods.GetMonitorInfo"/>; passing 0 makes the call fail
    /// silently with ERROR_INVALID_PARAMETER.
    /// </summary>
    private static readonly uint MonitorInfoCbSize =
        (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>();

    private readonly IntPtr _hwnd;
    private readonly IBarMenuDelegate _menuDelegate;
    private AppSettings _settings;

    /// <summary>
    /// Constructs the popup-menu service against the bar's HWND. The
    /// <paramref name="menuDelegate"/> implementation live in
    /// <c>MainWindow</c> so the service has no WPF shell dependencies.
    /// </summary>
    /// <param name="hwnd">MainWindow HWND, used as TrackPopupMenuEx owner + for monitor resolution.</param>
    /// <param name="settings">Live reference to the bar's settings source of truth.</param>
    /// <param name="menuDelegate">MainWindow-side implementations of the menu commands.</param>
    public BarPopupMenuService(IntPtr hwnd, AppSettings settings, IBarMenuDelegate menuDelegate)
    {
        _hwnd = hwnd;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _menuDelegate = menuDelegate ?? throw new ArgumentNullException(nameof(menuDelegate));
    }

    /// <summary>
    /// Updates the settings reference after <c>ApplySettings</c> /
    /// <c>ApplySettingsLive</c> in MainWindow. Mirrors the existing
    /// <c>AppBarService.BindSettings</c> hook so the rebuilt popup menu
    /// reflects the latest _settings state immediately on next right-click.
    /// </summary>
    public void BindSettings(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// WndProc delegate for <c>HwndSource.AddHook(...)</c>. Builds the
    /// native HMENU, forces dark chrome via uxtheme, positions it
    /// above/below the bar based on the bar's screen quadrant, runs
    /// TrackPopupMenuEx (which blocks until the user picks an item or
    /// dismisses), tears the menu down, and routes the chosen command
    /// to the <see cref="IBarMenuDelegate"/>. Returns IntPtr.Zero
    /// unconditionally -- WPF's default right-click -> ContextMenu
    /// behaviour has nothing to act on (we removed the WPF ContextMenu
    /// from MainWindow.xaml) so letting the message bubble costs nothing.
    /// </summary>
    public IntPtr WmRButtonUp(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_RBUTTONUP) return IntPtr.Zero;

        // Visibility.Collapsed: WPF won't deliver WM_RBUTTONUP for a
        // Collapsed window (no hit-test). The old `!= Visible` gate in
        // MainWindow.WmRButtonUp is redundant on the service side.

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return IntPtr.Zero;

        // Force the menu chrome to match the user's system theme.
        // On dark-mode systems we force dark (matches the .Kilobit
        // reference's hardcoded SetPreferredAppMode(2) call). On
        // light-mode systems we explicitly reset to default +
        // disable dark for our HWND so the OS renders the popup in
        // the user's light chrome rather than in a jarring dark
        // box. The three calls (SetPreferredAppMode, AllowDarkModeForWindow,
        // FlushMenuThemes) must run in that order either way -
        // FlushMenuThemes invalidates the menu theme cache so the
        // next CreatePopupMenu picks up the chosen mode. The whole
        // dance is wrapped in a try/catch so an older Windows that
        // doesn't export ShouldSystemUseDarkMode (#138 was added in
        // 1903) falls back to kil0bit parity (always force dark).
        ApplyMenuChromeMode(hwnd);

        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            BuildPopupMenu(hMenu);

            // TrackPopupMenuEx requires the calling thread to be the
            // foreground thread, otherwise the menu dismisses
            // immediately. WPF's HwndSource hook fires on the UI
            // thread, but right-clicking doesn't necessarily make
            // us the foreground window - call SetForegroundWindow
            // first so TrackPopupMenuEx retains focus until the
            // user picks an item.
            NativeMethods.SetForegroundWindow(hwnd);

            // Mirror the kil0bit position math: cursor-X (with
            // TPM_RIGHTALIGN so the menu's right edge meets the
            // cursor's right side); barTop-vs-monitor-midpoint
            // decides whether to pop the menu ABOVE (bottom half)
            // or BELOW (top half) the bar with a 4-pixel gap.
            int my;
            uint alignFlag;
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT wr) != 0)
            {
                IntPtr hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                NativeMethods.MONITORINFO mi = new NativeMethods.MONITORINFO
                {
                    cbSize = MonitorInfoCbSize
                };
                NativeMethods.GetMonitorInfo(hMon, ref mi);

                if (wr.Top > (mi.rcWork.Top + mi.rcWork.Bottom) / 2)
                {
                    // Bar in bottom half -> pop menu UP (menu's bottom
                    // edge sits 4 pixels above the bar's top edge)
                    my = wr.Top - 4;
                    alignFlag = NativeMethods.TPM_BOTTOMALIGN;
                }
                else
                {
                    // Bar in top half -> pop menu DOWN (menu's top
                    // edge sits 4 pixels below the bar's bottom edge)
                    my = wr.Bottom + 4;
                    alignFlag = NativeMethods.TPM_TOPALIGN;
                }
            }
            else
            {
                // Fallback: anchor to the cursor's Y if we couldn't
                // read the bar rect for any reason. The menu still
                // pops somewhere reasonable.
                my = cursor.Y;
                alignFlag = NativeMethods.TPM_TOPALIGN;
            }

            // TPM_RETURNCMD: TrackPopupMenuEx returns the selected
            // command id directly instead of sending WM_COMMAND.
            // TPM_NONOTIFY: don't fire WM_MENUSELECT/INITMENUPOPUP
            // notifications. Combined with the kil0bit 0x0002
            // (TPM_RIGHTALIGN), the menu's right edge sits at
            // cursor.X so a right-handed user clicking on the
            // bar's right side gets a menu that doesn't overflow
            // off the right of the monitor.
            int ch = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RIGHTALIGN | alignFlag,
                cursor.X,
                my,
                hwnd,
                IntPtr.Zero);

            if (ch != 0)
            {
                DispatchMenuCommand((uint)ch);
            }
        }
        finally
        {
            // Always destroy the menu even if TrackPopupMenuEx
            // threw - leaking HMENUs is one of the classic
            // GDI/desktop-process handle leaks.
            NativeMethods.DestroyMenu(hMenu);
        }

        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Populates <paramref name="hMenu"/> with the 10 menu items +
    /// 3 separators in the same order and command-ID space as
    /// <c>.Kilobit/OverlayWindow.cs</c> WM_RBUTTONUP. The four live
    /// toggles are appended with <c>MF_CHECKED | MF_STRING</c>
    /// (or just <c>MF_STRING</c> when off) so the user sees the
    /// current state of each toggle directly in the menu chrome -
    /// the kil0bit reference draws a checkmark next to enabled
    /// toggles via the OS-rendered MF_CHECKED bit.
    /// </summary>
    private void BuildPopupMenu(IntPtr hMenu)
    {
        // 1. Utility actions
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_SETTINGS, "Settings");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_TASKMGR, "Task Manager");

        // 2. Separator
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);

        // 3. View toggles. MF_CHECKED (0x0008) is the OS-rendered
        // checkmark glyph; mirrors kil0bit's
        //   AppendMenu(hMenu, (_config.Config.AlwaysOnTop ? 0x0008U : 0), 1008, "Keep on Top")
        // pattern exactly.
        NativeMethods.AppendMenu(hMenu,
            _settings.General.KeepOnTop ? NativeMethods.MF_CHECKED : 0,
            NativeMethods.IDM_KEEPONTOP, "Keep on Top");
        NativeMethods.AppendMenu(hMenu,
            _settings.General.HideInFullscreen ? NativeMethods.MF_CHECKED : 0,
            NativeMethods.IDM_HIDEFULLSCREEN, "Hide in Fullscreen");
        NativeMethods.AppendMenu(hMenu,
            _settings.Window.LockPosition ? NativeMethods.MF_CHECKED : 0,
            NativeMethods.IDM_LOCK, "Lock Position");
        NativeMethods.AppendMenu(hMenu,
            _settings.Window.StickToTaskbar ? NativeMethods.MF_CHECKED : 0,
            NativeMethods.IDM_SNAP, "Snap to Taskbar");

        // 4. Separator
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);

        // 5. About (kil0bit cmd 1003 -> opens Settings + auto-navigates)
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_ABOUT, "About");

        // 6. Separator
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);

        // 7. Restart (WinMeters extension cmd 1010, beyond the
        //    kil0bit 1001-1009 ID space). Power-user request: lets
        //    the user pick up settings changes without manually
        //    closing + reopening the bar.
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_RESTART, "Restart");

        // 8. Separator
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);

        // 9. Exit
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, NativeMethods.IDM_EXIT, "Exit");
    }

    /// <summary>
    /// Dispatches the command id returned by
    /// <see cref="NativeMethods.TrackPopupMenuEx"/> to the equivalent
    /// action. Mirrors the kil0bit switch (cmd 1001 = Settings, 1002 =
    /// Task Manager, 1003 = About -> Settings, 1004 = Exit, 1006 = Lock
    /// toggle, 1007 = Snap toggle, 1008 = Keep-on-top toggle, 1009 =
    /// Hide-in-fullscreen toggle, 1010 = WinMeters-extension Restart).
    /// Each toggle calls back into the <see cref="IBarMenuDelegate"/>
    /// so the state mutation + side-effect + persistence stays in
    /// MainWindow (where <c>_settings</c> + the WPF state live).
    /// </summary>
    private void DispatchMenuCommand(uint cmd)
    {
        switch (cmd)
        {
            case NativeMethods.IDM_SETTINGS:
                _menuDelegate.HandleShowSettings();
                break;

            case NativeMethods.IDM_TASKMGR:
                _menuDelegate.HandleOpenTaskManager();
                break;

            case NativeMethods.IDM_ABOUT:
                // Cmd 1003 (About) opens the dedicated AboutWindow --
                // brand wordmark + version + predecessor row, single
                // OK button. Single-instance gate via the parallel
                // OpenAboutWindow helper; see that method for the
                // shared OpenSettings / OpenAboutWindow pattern.
                _menuDelegate.HandleOpenAbout();
                break;

            case NativeMethods.IDM_EXIT:
                _menuDelegate.HandleExit();
                break;

            case NativeMethods.IDM_LOCK:
                _menuDelegate.HandleToggleLock();
                break;

            case NativeMethods.IDM_SNAP:
                _menuDelegate.HandleToggleSnap();
                break;

            case NativeMethods.IDM_KEEPONTOP:
                _menuDelegate.HandleToggleKeepOnTop();
                break;

            case NativeMethods.IDM_HIDEFULLSCREEN:
                _menuDelegate.HandleToggleHideInFullscreen();
                break;

            case NativeMethods.IDM_RESTART:
                _menuDelegate.HandleRestart();
                break;

            default:
                WinMeters.Log.D($"BarPopupMenuService.DispatchMenuCommand: unknown cmd {cmd}");
                break;
        }
    }

    /// <summary>Launches taskmgr.exe via the shell. Matches the kil0bit cmd-1002 handler. Public so MainWindow's IBarMenuDelegate.HandleOpenTaskManager can call it without a service instance.</summary>
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
        catch (Exception ex)
        {
            WinMeters.Log.D($"LaunchTaskManager: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the dark/light menu-chrome mode to match the user's
    /// system theme. On dark-mode systems we force dark chrome
    /// (matches the .Kilobit reference's hardcoded
    /// SetPreferredAppMode(2)). On light-mode systems we reset
    /// the preferred mode to Default and disable dark for our HWND
    /// so the OS renders the popup in the user's light chrome.
    /// Wrapped in a try/catch so an older Windows that doesn't
    /// export uxtheme #138 (ShouldSystemUseDarkMode, added in
    /// 1903) falls back to the kil0bit behaviour of always
    /// forcing dark.
    /// </summary>
    private static void ApplyMenuChromeMode(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.ShouldSystemUseDarkMode() != 0)
            {
                // System is in dark mode - force dark chrome.
                NativeMethods.SetPreferredAppMode(NativeMethods.PREFERRED_APP_MODE_FORCE_DARK);
                NativeMethods.AllowDarkModeForWindow(hwnd, true);
            }
            else
            {
                // System is in light mode - reset to default +
                // disable dark for our HWND so the OS paints the
                // popup in the user's light chrome.
                NativeMethods.SetPreferredAppMode(NativeMethods.PREFERRED_APP_MODE_DEFAULT);
                NativeMethods.AllowDarkModeForWindow(hwnd, false);
            }
            NativeMethods.FlushMenuThemes();
        }
        catch (System.EntryPointNotFoundException)
        {
            // Older Windows without uxtheme #138 - fall back to
            // kil0bit parity (always force dark). The SetPreferredAppMode
            // / AllowDarkModeForWindow / FlushMenuThemes trio is the
            // same one kil0bit calls unconditionally.
            NativeMethods.SetPreferredAppMode(NativeMethods.PREFERRED_APP_MODE_FORCE_DARK);
            NativeMethods.AllowDarkModeForWindow(hwnd, true);
            NativeMethods.FlushMenuThemes();
        }
    }
}
