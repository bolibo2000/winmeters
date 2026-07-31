using System.Windows;
using System.Windows.Interop;

namespace WinMeters.Services;

internal sealed class WindowPlacementService
{
    private readonly Window _window;
    private AppSettings _settings;
    private const double FirstLaunchDip = 100.0;

    public WindowPlacementService(Window window, AppSettings settings)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void BindSettings(AppSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public double GetX() => _settings.Window.PositionX ?? FirstLaunchDip;
    public double GetY() => _settings.Window.PositionY ?? FirstLaunchDip;

    public void SavePositionPx(int xPx, int yPx)
    {
        _settings.Window.PositionX = xPx;
        _settings.Window.PositionY = yPx;
    }

    public void SaveCurrentDips()
    {
        _settings.Window.PositionX = _window.Left;
        _settings.Window.PositionY = _window.Top;
    }

    public (int Left, int Top, int Right, int Bottom)? GetMonitorBoundsForWindow()
    {
        try
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return null;

            IntPtr hmon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return null;

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            if (!NativeMethods.GetMonitorInfo(hmon, ref info)) return null;

            return (info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right, info.rcMonitor.Bottom);
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"WindowPlacementService.GetMonitorBoundsForWindow: {ex.Message}");
            return null;
        }
    }
}
