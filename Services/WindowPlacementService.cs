using System.Windows;
using System.Windows.Interop;

namespace WinMeters.Services;

/// <summary>
/// WinMeters-style window placement shim. Compared to the legacy WinMeters
/// <c>WindowPlacementService</c> this service is intentionally thin: it owns
/// <see cref="AppSettings.WindowSettings.PositionX"/> / <c>PositionY</c> as the
/// single source of truth for the saved window rect, and it can resolve the
/// monitor an HWND lives on via <c>MonitorFromWindow</c> +
/// <see cref="NativeMethods.MONITORINFO"/>.
/// <para>
/// There is no <c>CenterOnTargetMonitor</c>, no
/// <c>ClampToTargetMonitor</c>, and no <c>RestorePosition</c>. Centring
/// happens in <see cref="AppBarService.HwndHook"/> via the
/// <c>WM_WINDOWPOSCHANGING</c> Y-clamp (matching kil0bit's
/// <c>OverlayWindow.WndProc</c>), and clamping on save-load happens implicitly
/// because <see cref="AppBarService"/> owns the live taskbar reading.
/// </para>
/// </summary>
internal sealed class WindowPlacementService
{
    private readonly Window _window;
    private AppSettings _settings;

    /// <summary>First-launch fallback range (DIPs) — visible on every standard config.</summary>
    private const double FirstLaunchDip = 100.0;

    public WindowPlacementService(Window window, AppSettings settings)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Replaces the settings instance this service reads from. Call after
    /// <see cref="AppSettings.Load"/> returns a fresh instance so the
    /// service's reference doesn't go stale.
    /// </summary>
    public void BindSettings(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Saved X (WPF DIPs). Falls back to a safe first-launch position when unset.</summary>
    public double GetX() => _settings.Window.PositionX ?? FirstLaunchDip;

    /// <summary>Saved Y (WPF DIPs). Falls back to a safe first-launch position when unset.</summary>
    public double GetY() => _settings.Window.PositionY ?? FirstLaunchDip;

    /// <summary>
    /// Persistence helper for <see cref="AppBarService"/> to call from
    /// <c>WM_EXITSIZEMOVE</c>. Stores in physical pixels because that's the
    /// unit the WndProc recv'd — WPF will treat that as DIPs on the next
    /// <c>Window.Left/Top</c> assignment, but the value is kept verbatim so a
    /// switch back to the same monitor lands on the same screen pixel.
    /// </summary>
    public void SavePositionPx(int xPx, int yPx)
    {
        _settings.Window.PositionX = xPx;
        _settings.Window.PositionY = yPx;
    }

    /// <summary>
    /// Saves the WPF Window's current <see cref="Window.Left"/>/<see cref="Window.Top"/>
    /// (already in DIPs). Used by the legacy <see cref="MainWindow.SavePosition"/>
    /// exit/OK path so menu-driven saves still work.
    /// </summary>
    public void SaveCurrentDips()
    {
        _settings.Window.PositionX = _window.Left;
        _settings.Window.PositionY = _window.Top;
    }

    /// <summary>
    /// Resolves the monitor the WPF window lives on and returns its
    /// <see cref="NativeMethods.MONITORINFO.rcMonitor"/> rect (physical pixels,
    /// virtual-screen coordinates). Returns <c>null</c> if the HWND is not yet
    /// created or if <c>GetMonitorInfo</c> fails — callers are expected to
    /// gracefully fall back to a centred placement.
    /// </summary>
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
            if (!NativeMethods.GetMonitorInfo(hmon, ref info))
                return null;

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
