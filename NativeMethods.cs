using System.Runtime.InteropServices;

namespace WinMeters
{
    /// <summary>
    /// Windows API interop methods organized by functionality.
    /// </summary>
    internal static class NativeMethods
    {
        #region Window Management

        // Windows message constants for activation/focus handling
        public const int WM_ACTIVATE = 0x0006;
        public const int WM_ACTIVATEAPP = 0x001C;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        /// <summary>
        /// Retrieves the handle to a window that has the specified relationship
        /// (Z-order or owner) to the given window. Used by kil0bit-style z-order
        /// enforcement to ask: "is something else sitting above me right now?".
        /// </summary>
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        /// <summary>Retrieves the window above us in Z-order.</summary>
        public const uint GW_HWNDPREV = 3;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "GetClassNameW")]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        /// <summary>Sets / clears the owner of a top-level window (same as SetParent for owned popups).</summary>
        public const int GWL_HWNDPARENT = -8;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        /// <summary>
        /// Sets a window long value (pointer-sized HWND / LPTR-safe). Mirrors the
        /// native <c>SetWindowLongPtrW</c> exported by user32 on every Windows
        /// version since Windows 2000 in both 32-bit and 64-bit builds. Use this
        /// instead of <see cref="SetWindowLong"/> for any index whose value is an
        /// HWND / pointer (such as GWL_HWNDPARENT); SetWindowLong truncates the
        /// upper 32 bits on x64.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        /// <summary>Per-window DPI (96 = 100%, 192 = 200%).</summary>
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DISPLAYCHANGE = 0x007E;
        /// <summary>Sent before a window's position/size changes; lets us modify WINDOWPOS in-place.</summary>
        public const int WM_WINDOWPOSCHANGING = 0x0046;
        /// <summary>Sent after a window's position/size changes.</summary>
        public const int WM_WINDOWPOSCHANGED = 0x0047;
        /// <summary>Sent when the user releases the mouse after a drag/resize — kil0bit saves X/Y here.</summary>
        public const int WM_EXITSIZEMOVE = 0x0232;

        /// <summary>
        /// Places the window at the top of the Z-order.
        /// </summary>
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        /// <summary>
        /// Places the window at the top of the non-topmost Z-order.
        /// This keeps the window above normal windows but below any HWND_TOPMOST windows.
        /// </summary>
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        /// <summary>
        /// Window position flags for SetWindowPos.
        /// </summary>
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        // Window style constants (non-extended)
        public const int GWL_STYLE = -16;
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_VISIBLE = 0x10000000;

        // GetWindow relationship codes
        public const uint GW_OWNER = 4;

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// Returns the HMONITOR that the window lies on (or, with
        /// <see cref="MONITOR_DEFAULTTONEAREST"/>, the closest one). WinMeters-style
        /// replacement for <c>System.Windows.Forms.Screen.AllScreens</c> — resolves the
        /// monitor an HWND is currently on without depending on WinForms.
        /// </summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        /// <summary>MONITOR_DEFAULTTONEAREST — return the monitor whose rect contains (or is nearest to) the window.</summary>
        public const uint MONITOR_DEFAULTTONEAREST = 0x00000001;

        /// <summary>
        /// Per-monitor info. <c>rcMonitor</c> is the full display rect in virtual-screen
        /// coordinates; <c>rcWork</c> excludes the taskbar / appbar strip. We use rcMonitor
        /// (matching kil0bit's OverlayWindow.IsShellWindow path) so dock math is consistent
        /// across users with taskbars on different edges.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// WINDOWPOS — passed by reference through lParam of WM_WINDOWPOSCHANGING /
        /// WM_WINDOWPOSCHANGED. Mutating <c>x</c>/<c>y</c>/<c>cx</c>/<c>cy</c>/<c>flags</c>
        /// in WM_WINDOWPOSCHANGING and StructureToPtr'ing the struct back is the standard
        /// way to clamp the shell's view of a window before it commits. Adopted from
        /// kil0bit's OverlayWindow.WndProc.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [DllImport("user32.dll")]
        public static extern int GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool EnumThreadWindows(int dwThreadId, EnumThreadProc lpfn, IntPtr lParam);

        public delegate bool EnumThreadProc(IntPtr hwnd, IntPtr lParam);

        // Extended window style constants
        public const int GWL_EXSTYLE = -20;
        public const uint WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

        #endregion

        #region AppBar (SHAppBarMessage)

        /// <summary>Register a new appbar and store the given HWND + callback message.</summary>
        public const uint ABM_NEW = 0x00000000;
        /// <summary>Unregister an existing appbar.</summary>
        public const uint ABM_REMOVE = 0x00000001;
        /// <summary>Query available screen space for the given edge + RECT.</summary>
        public const uint ABM_QUERYPOS = 0x00000002;
        /// <summary>Set the appbar position; the shell may clip the requested RECT.</summary>
        public const uint ABM_SETPOS = 0x00000003;
        /// <summary>Notify the shell that the app wants autohide suppressed when the user is on it.</summary>
        public const uint ABM_SETAUTOHIDEHIDDEN = 0x0000000B;
        /// <summary>Tell the shell our appbar's window position just changed.</summary>
        public const uint ABM_WINDOWPOSCHANGED = 0x00000009;

        /// <summary>
        /// Fixed callback message id used by kil0bit (and many shells) to receive
        /// appbar notifications via SHAppBarMessage(ABM_NEW). The shell sends this
        /// exact id to our HWND whenever the taskbar's layout, fullscreen state,
        /// or arrangement changes. We use this constant so the message id never
        /// shifts across sessions and matches kil0bit's heuristic exactly.
        /// </summary>
        public const uint WM_APPBAR_CALLBACK = 0x0502;

        public const uint ABN_POSCHANGED = 0x00000001;
        public const uint ABN_FULLSCREENAPP = 0x00000002;
        public const uint ABN_WINDOWARRANGE = 0x00000003;
        public const uint ABN_STATECHANGE = 0x00000009;

        public const int ABE_LEFT = 0;
        public const int ABE_TOP = 1;
        public const int ABE_RIGHT = 2;
        public const int ABE_BOTTOM = 3;

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        /// <summary>
        /// APPBARDATA — passed by ref to <see cref="SHAppBarMessage"/>. The shell reads
        /// cbSize on entry and writes the adjusted RECT back on ABM_QUERYPOS / ABM_SETPOS.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        #endregion

        #region Hotkey

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Windows message ID for hotkey events.
        /// </summary>
        public const uint WM_HOTKEY = 0x0312;

        /// <summary>
        /// Modifier flags for RegisterHotKey.
        /// </summary>
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        #endregion

        #region Low-Level Keyboard Hook

        /// <summary>
        /// Hook type for low-level keyboard hook (WH_KEYBOARD_LL = 13).
        /// </summary>
        public const int WH_KEYBOARD_LL = 13;

        /// <summary>
        /// Windows message for keyboard input.
        /// </summary>
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;

        /// <summary>
        /// Virtual key code for 'X' key.
        /// </summary>
        public const int VK_X = 0x58;

        /// <summary>
        /// Virtual key code for 'M' key.
        /// </summary>
        public const int VK_M = 0x4D;

        /// <summary>
        /// Virtual key code for Control key.
        /// </summary>
        public const int VK_CONTROL = 0x11;

        /// <summary>
        /// Virtual key code for Menu (Alt) key.
        /// </summary>
        public const int VK_MENU = 0x12;

        /// <summary>
        /// Called when a keyboard event occurs. Must return the next hook in chain.
        /// </summary>
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Retrieves the current state of the keyboard.
        /// </summary>
        [DllImport("user32.dll")]
        public static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        #endregion

        #region DWM Window Attributes

        /// <summary>
        /// Sets a DWM window attribute on the given HWND. Return value is an
        /// HRESULT (cast the <see cref="System.Runtime.InteropServices.Marshal"/>
        /// error to a <see cref="int"/> via <c>Win32Exception</c> if the call
        /// fails). Available since Windows 10 v1607 (build 14955).
        ///
        /// WinMeters uses this in <see cref="Services.AppBarService.AttachToTaskbar"/>
        /// to set <see cref="DWMWA_TRANSITIONS_FORCEDISABLED"/> = 1, mirroring
        /// kil0bit's behaviour so the system taskbar does not re-fade/animate
        /// every time we snap above it.
        /// </summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>DWMWA_TRANSITIONS_FORCEDISABLED — disable DWM transition animations for this HWND.</summary>
        public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

        #endregion

        #region User Notification State

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        #endregion

        #region Cursor and Per-Monitor DPI

        /// <summary>
        /// Reads the cursor's position in virtual-screen device pixels. The
        /// returned coords are relative to the primary monitor's origin
        /// (top-left of the virtual desktop), regardless of which monitor the
        /// cursor sits on. Combined with <see cref="MonitorFromPoint"/> +
        /// <see cref="GetDpiForMonitor"/>, this gives us the cursor's true
        /// screen location and the DPI scale of the monitor it sits on, so we
        /// can convert pixels → DIPs without the per-monitor DPI virtualization
        /// ambiguity that complicates <c>Mouse.GetPosition</c> in WPF.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        /// <summary>
        /// Returns the HMONITOR that contains (or is nearest to) the given
        /// point. With <see cref="MONITOR_DEFAULTTONEAREST"/> resolves to the
        /// same monitor Win32 considers the cursor's "current" monitor even
        /// when the point is on a monitor boundary.
        /// </summary>
        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        /// <summary>
        /// Queries the effective DPI of the monitor that HMONITOR refers to.
        /// Effective DPI is the system's scaled DPI (e.g. 144 = 150%) and
        /// matches the scale factor WPF uses when positioning its windows.
        /// </summary>
        [DllImport("shcore.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

        #endregion

        #region Memory Information

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] ref MEMORYSTATUSEX lpBuffer);

        #endregion

        #region Popup Menu (HMENU)

        /// <summary>
        /// Creates a popup menu (a top-level menu not attached to a
        /// menubar). The returned handle is owned by the caller; pair with
        /// <see cref="DestroyMenu"/> when done. Mirrors
        /// .Kilobit/OverlayWindow.cs CreatePopupMenu.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        /// <summary>
        /// Appends a menu item to a menu bar, drop-down menu, or submenu.
        /// Pass MF_STRING (0) for a text item, MF_SEPARATOR (0x0800) for a
        /// divider, and combine MF_CHECKED (0x0008) for a checkable item
        /// that's currently on. The <c>lpNewItem</c> string is allowed to
        /// be null when the flags include MF_SEPARATOR. Matches
        /// .Kilobit/OverlayWindow.cs AppendMenu verbatim, including the
        /// Unicode charset.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

        /// <summary>
        /// Displays a shortcut menu at the specified location and tracks
        /// the selection. With TPM_RETURNCMD the function returns the
        /// selected command ID instead of sending WM_COMMAND; with
        /// TPM_NONOTIFY no WM_COMMAND / WM_MENUSELECT notifications are
        /// sent to the owner either. The function runs its own message
        /// pump and blocks the calling thread until the menu closes.
        /// Matches .Kilobit/OverlayWindow.cs TrackPopupMenuEx.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        /// <summary>
        /// Destroys the specified menu and frees any memory the menu
        /// occupied. Required for menus created with CreatePopupMenu.
        /// Matches .Kilobit/OverlayWindow.cs DestroyMenu.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        /// <summary>
        /// Brings the thread that created the specified window into the
        /// foreground and activates the window. Required before
        /// TrackPopupMenuEx or the popup menu won't receive keyboard
        /// focus and will be dismissed immediately. Matches
        /// .Kilobit/OverlayWindow.cs SetForegroundWindow.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        // AppendMenu / TrackPopupMenuEx flag constants. Numeric values
        // match Win32 (winuser.h). MF_UNCHECKED is 0 so omitted entirely
        // (the kil0bit reference uses `... : 0` for unchecked items).
        public const uint MF_STRING = 0x0000;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint MF_CHECKED = 0x0008;

        // TrackPopupMenuEx flag constants. Numeric values match Win32 (winuser.h).
        public const uint TPM_LEFTALIGN = 0x0000;
        public const uint TPM_TOPALIGN = 0x0000;
        public const uint TPM_BOTTOMALIGN = 0x0020;
        /// <summary>TPM_RIGHTALIGN — right-align the popup relative to the X coord. Matches the kil0bit reference's 0x0002 flag in its TrackPopupMenuEx call.</summary>
        public const uint TPM_RIGHTALIGN = 0x0002;
        public const uint TPM_RETURNCMD = 0x0100;
        public const uint TPM_NONOTIFY = 0x0080;

        /// <summary>
        /// Command IDs for the popup menu items. Matches the kil0bit
        /// reference port's WM_RBUTTONUP handler (cmd 1001-1009) so the
        /// dispatch order / semantics are identical to .Kilobit.
        /// </summary>
        public const uint IDM_SETTINGS = 1001;
        public const uint IDM_TASKMGR = 1002;
        public const uint IDM_ABOUT = 1003;
        public const uint IDM_EXIT = 1004;
        public const uint IDM_LOCK = 1006;
        public const uint IDM_SNAP = 1007;
        public const uint IDM_KEEPONTOP = 1008;
        public const uint IDM_HIDEFULLSCREEN = 1009;
        /// <summary>IDM_RESTART (1010) — WinMeters extension beyond the kil0bit 1001-1009 ID space. Restarts the bar so the user can pick up settings changes without manual close/reopen.</summary>
        public const uint IDM_RESTART = 1010;

        #endregion

        #region UXTheme (dark mode)

        /// <summary>
        /// uxtheme.dll ordinal #135: sets the preferred app mode for
        /// subsequent window/menu creation. 2 = ForceDark. Used to make
        /// the OS-themed native popup menu render in dark chrome
        /// regardless of the user's system theme setting. Matches
        /// .Kilobit/OverlayWindow.cs SetPreferredAppMode.
        /// </summary>
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        public static extern int SetPreferredAppMode(int appMode);

        /// <summary>
        /// uxtheme.dll ordinal #133: toggles dark mode for the title bar /
        /// menu chrome of a specific HWND. Combined with
        /// SetPreferredAppMode(2) and FlushMenuThemes() the popup menu
        /// chrome paints dark on systems where dark mode is not the
        /// default. Matches .Kilobit/OverlayWindow.cs AllowDarkModeForWindow.
        /// </summary>
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowDarkModeForWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

        /// <summary>
        /// uxtheme.dll ordinal #136: flushes the menu theme cache so the
        /// next menu creation picks up the dark mode setting. Must be
        /// called AFTER SetPreferredAppMode / AllowDarkModeForWindow and
        /// BEFORE TrackPopupMenuEx for the dark chrome to take effect.
        /// Matches .Kilobit/OverlayWindow.cs FlushMenuThemes.
        /// </summary>
        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        public static extern void FlushMenuThemes();

        /// <summary>
        /// uxtheme.dll ordinal #138: returns 1 if the system is currently in
        /// dark mode, 0 if light. Available since Windows 10 1903 (May 2019).
        /// WinMeters reads this in <c>WmRButtonUp</c> to decide whether to
        /// force the popup menu chrome dark (matches kil0bit) or leave it
        /// alone (so light-mode users see the OS-default light chrome). The
        /// default fallback (return 0) is treated as "light" by the caller;
        /// if the function is unavailable on an older Windows version the
        /// caller wraps in try/catch and defaults to forcing dark to match
        /// kil0bit behaviour.
        /// </summary>
        [DllImport("uxtheme.dll", EntryPoint = "#138")]
        public static extern int ShouldSystemUseDarkMode();

        // SetPreferredAppMode argument values (uxtheme.h PreferredAppMode enum).
        // WinMeters uses Default (0) on light-mode systems so the OS renders
        // the popup menu in the user's chosen chrome, and ForceDark (2) on
        // dark-mode systems to match the kil0bit reference port's chrome.
        public const int PREFERRED_APP_MODE_DEFAULT = 0;
        public const int PREFERRED_APP_MODE_ALLOW_DARK = 1;
        public const int PREFERRED_APP_MODE_FORCE_DARK = 2;
        public const int PREFERRED_APP_MODE_FORCE_LIGHT = 3;

        #endregion

        #region System Color

        /// <summary>
        /// COLOR_MENU (= 4) — index used with <see cref="GetSysColor"/> to
        /// read the OS's current menu-background brush. Win32 returns this
        /// as a COLORREF (0x00BBGGRR). Used by ColorHelper.GetMenuBackgroundBrush
        /// to make the SettingsWindow background match the native HMENU that
        /// the bar's RMB popup paints, so the two surfaces look uniform
        /// regardless of which Windows theme (dark / light / custom accent)
        /// the user is currently running.
        /// </summary>
        public const int COLOR_MENU = 4;

        /// <summary>
        /// Returns the red / green / blue components of one of the system
        /// system colors as a COLORREF-encoded <see cref="int"/> (low byte = R,
        /// middle byte = G, high byte = B; top byte reserved / 0). Callers
        /// convert to WPF Brushes via Color.FromArgb(255, r, g, b). The
        /// return is a built-in Win32 API value -- changes immediately when
        /// the OS theme flips, no round-trip through a Win32 Brush / HBRUSH
        /// reference needed (which would leak handles).
        /// </summary>
        [DllImport("user32.dll")]
        public static extern int GetSysColor(int nIndex);

        #endregion

        #region Types

        /// <summary>
        /// Window rectangle coordinates.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// Low-level keyboard hook structure (KBDLLHOOKSTRUCT).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// User notification state enumeration (from ShellUser.h).
        /// </summary>
        public enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        /// <summary>
        /// Memory status structure for GlobalMemoryStatusEx (104 bytes).
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>
        /// 32-bit screen coordinate (SM_CXCURSOR-style struct, used by Win32
        /// GetCursorPos / MonitorFromPoint). Fields are int (not int32_t) to
        /// match the Win32 POINT layout, and use int X/Y for symmetry with the
        /// existing RECT struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// DPI kinds that <see cref="GetDpiForMonitor"/> can return.
        /// MDT_EFFECTIVE_DPI is the system-scaled DPI, which is what WPF uses
        /// for window positioning and matches our tooltip math's intent.
        /// </summary>
        public enum MONITOR_DPI_TYPE
        {
            MDT_EFFECTIVE_DPI = 0,
            MDT_ANGULAR_DPI = 1,
            MDT_RAW_DPI = 2,
            MDT_DEFAULT = MDT_EFFECTIVE_DPI
        }

        #endregion
    }
}
