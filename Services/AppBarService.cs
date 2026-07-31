using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinMeters.Services;

internal sealed class AppBarService : IDisposable
{
    private readonly Window _window;
    private AppSettings _settings;
    private IntPtr _hwnd;
    private IntPtr _taskbarHwnd;
    private uint _currentDpi = 96;
    private float _dpiScale = 1.0f;
    private bool _registered;
    private bool _disposed;

    public float DpiScale => _dpiScale;
    public bool IsRegistered => _registered;
    public bool IsTaskbarStuck => _settings.Window.StickToTaskbar;

    private const int BarHeightNormalDips = 40;

    public AppBarService(Window window, AppSettings settings)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void BindSettings(AppSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public bool AttachToTaskbar()
    {
        if (_disposed) return false;
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero) return false;

        try
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            _taskbarHwnd = taskbar;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, taskbar);
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            _currentDpi = NativeMethods.GetDpiForWindow(_hwnd);
            if (_currentDpi == 0) _currentDpi = 96;
            _dpiScale = _currentDpi / 96.0f;

            var data = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = _hwnd,
                uCallbackMessage = NativeMethods.WM_APPBAR_CALLBACK,
                uEdge = NativeMethods.ABE_BOTTOM,
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
            _registered = true;

            int disableTransitions = 1;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref disableTransitions, sizeof(int));

            AlignToTaskbarCenterPx();
            return true;
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"AppBarService.AttachToTaskbar failed: {ex}");
            return false;
        }
    }

    public void FreeFloat()
    {
        if (_disposed) return;
        try
        {
            if (_registered && _hwnd != IntPtr.Zero)
            {
                var data = new NativeMethods.APPBARDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                    hWnd = _hwnd,
                };
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
                _registered = false;
            }
            if (_hwnd != IntPtr.Zero)
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, IntPtr.Zero);
            _taskbarHwnd = IntPtr.Zero;
        }
        catch (Exception ex) { WinMeters.Log.D($"AppBarService.FreeFloat failed: {ex}"); }
    }

    public void ReAttach()
    {
        if (_disposed) return;
        FreeFloat();
        if (IsTaskbarStuck) AttachToTaskbar();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { FreeFloat(); } catch (Exception ex) { WinMeters.Log.D($"AppBarService.Dispose FreeFloat: {ex.Message}"); }
    }

    public void ApplyIntegrationState(double savedXDip, double savedYDip)
    {
        if (_disposed) return;

        if (IsTaskbarStuck)
        {
            _window.Left = savedXDip;
            if (!_registered) { AttachToTaskbar(); return; }
            AlignToTaskbarCenterPx();
            return;
        }

        FreeFloat();
        _window.Left = savedXDip;
        _window.Top = savedYDip;
    }

    public IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        _hwnd = hwnd;

        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && IsTaskbarStuck && lParam != IntPtr.Zero)
        {
            try
            {
                var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                if (ClampYToTaskbarPx(ref pos))
                    Marshal.StructureToPtr(pos, lParam, false);
            }
            catch (Exception ex) { WinMeters.Log.D($"AppBarService WM_WINDOWPOSCHANGING: {ex.Message}"); }
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_WINDOWPOSCHANGED)
        {
            if (_registered && _hwnd != IntPtr.Zero)
            {
                var data = new NativeMethods.APPBARDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                    hWnd = _hwnd,
                };
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_WINDOWPOSCHANGED, ref data);
            }
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_EXITSIZEMOVE)
        {
            PersistCurrentPositionPx();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DPICHANGED)
        {
            _currentDpi = NativeMethods.GetDpiForWindow(_hwnd);
            if (_currentDpi == 0) _currentDpi = 96;
            _dpiScale = _currentDpi / 96.0f;
            if (IsTaskbarStuck) AlignToTaskbarCenterPx();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DISPLAYCHANGE || msg == NativeMethods.WM_SETTINGCHANGE)
        {
            if (IsTaskbarStuck) AlignToTaskbarCenterPx();
            return IntPtr.Zero;
        }

        if ((uint)msg == NativeMethods.WM_APPBAR_CALLBACK)
        {
            uint notification = (uint)wParam.ToInt64();
            switch (notification)
            {
                case NativeMethods.ABN_POSCHANGED:
                case NativeMethods.ABN_WINDOWARRANGE:
                    _window.Dispatcher.BeginInvoke(new Action(AlignToTaskbarCenterPx),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    break;

                case NativeMethods.ABN_FULLSCREENAPP:
                    if (!_settings.General.HideInFullscreen) break;
                    bool fullscreen = lParam.ToInt64() != 0;
                    if (fullscreen)
                        _window.Visibility = Visibility.Collapsed;
                    else if (!_settings.Window.IsHiddenByUser)
                        _window.Visibility = Visibility.Visible;
                    break;
            }
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void PersistCurrentPositionPx()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r) == 0) return;

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        if (scale <= 0) scale = 1.0;

        _settings.Window.PositionX = r.Left / scale;
        _settings.Window.PositionY = r.Top / scale;
    }

    private bool ClampYToTaskbarPx(ref NativeMethods.WINDOWPOS pos)
    {
        if (!TryGetTaskbarRect(out NativeMethods.RECT tb, out _)) return false;
        int winHPx = ComputeBarHeightPx();
        int cyPx = ComputeCenteredY(tb, winHPx);
        if (pos.y == cyPx) return false;
        pos.y = cyPx;
        return true;
    }

    private static bool TryGetTaskbarRect(out NativeMethods.RECT rect, out IntPtr hwnd)
    {
        rect = default;
        hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return false;
        return NativeMethods.GetWindowRect(hwnd, out rect) != 0;
    }

    private static int ComputeCenteredY(NativeMethods.RECT tb, int winHPx)
    {
        if (winHPx <= 0) return tb.Top;
        int tbH = tb.Bottom - tb.Top;
        int cyPx = tb.Top + (tbH - winHPx) / 2;
        if (cyPx + winHPx > tb.Bottom) cyPx = tb.Bottom - winHPx;
        if (cyPx < tb.Top) cyPx = tb.Top;
        return cyPx;
    }

    private int ComputeBarHeightPx()
    {
        double scale = Math.Max(0.25, _settings.General.Scale > 0 ? _settings.General.Scale : 1.0);
        return (int)Math.Round(BarHeightNormalDips * (double)_dpiScale * scale);
    }

    private void AlignToTaskbarCenterPx()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!TryGetTaskbarRect(out NativeMethods.RECT tb, out IntPtr taskbar)) return;

        int winHPx = ComputeBarHeightPx();
        int cyPx = ComputeCenteredY(tb, winHPx);

        int xPx = (int)(_settings.Window.PositionX ?? 100);
        if (NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT ourRect) != 0)
            xPx = ourRect.Left;

        if (taskbar != _taskbarHwnd)
        {
            _taskbarHwnd = taskbar;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, taskbar);
        }

        _settings.Window.PositionY = cyPx / _dpiScale;

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            xPx, cyPx, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
