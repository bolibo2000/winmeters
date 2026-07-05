using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WinMeters.Services;

/// <summary>
/// Manages a single system-wide hotkey (Ctrl+Alt+Shift+M) for toggling the WinMeters window.
/// RegisterHotKey works even when fullscreen games are focused, which is why this code path
/// exists in addition to whatever input the main window receives.
/// </summary>
/// <remarks>
/// This is intentionally a thin wrapper around <c>RegisterHotKey</c> / <c>UnregisterHotKey</c>.
/// Behaviour changes should happen in <see cref="NativeMethods"/> or here, but not in
/// <see cref="MainWindow"/>, so the contract is portable to other panels later.
/// </remarks>
internal sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action _onHotkeyPressed;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// Creates a service that registers the configured hotkey against <paramref name="hwnd"/>.
    /// </summary>
    /// <param name="hwnd">Window handle to receive WM_HOTKEY notifications.</param>
    /// <param name="onHotkeyPressed">Callback invoked on the UI thread when the hotkey fires.</param>
    public HotkeyService(IntPtr hwnd, Action onHotkeyPressed)
    {
        _hwnd = hwnd;
        _onHotkeyPressed = onHotkeyPressed ?? throw new ArgumentNullException(nameof(onHotkeyPressed));
    }

    /// <summary>
    /// Hook procedure for the owning window's WndProc. Returns IntPtr.Zero after handling.
    /// </summary>
    public IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == Constants.Hotkey.HotkeyId)
        {
            _onHotkeyPressed();
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Registers the Ctrl+Alt+Shift+M hotkey. Safe to call once; subsequent calls are no-ops.
    /// </summary>
    public void Register()
    {
        if (_registered || _disposed) return;

        try
        {
            // MOD_CONTROL | MOD_ALT | MOD_SHIFT = 0x0007
            _registered = NativeMethods.RegisterHotKey(
                _hwnd,
                Constants.Hotkey.HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                NativeMethods.VK_M);

            if (_registered)
            {
                WinMeters.Log.D("HotkeyService: Ctrl+Alt+Shift+M registered.");
            }
            else
            {
                int errorCode = Marshal.GetLastWin32Error();
                WinMeters.Log.D($"HotkeyService: RegisterHotKey failed (error {errorCode}). Another app may own Ctrl+Alt+Shift+M.");
            }
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"HotkeyService.Register exception: {ex}");
        }
    }

    /// <summary>
    /// Unregisters the hotkey if it was successfully registered.
    /// </summary>
    public void Unregister()
    {
        if (!_registered || _disposed) return;

        try
        {
            NativeMethods.UnregisterHotKey(_hwnd, Constants.Hotkey.HotkeyId);
            _registered = false;
            WinMeters.Log.D("HotkeyService: unregistered.");
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"HotkeyService.Unregister exception: {ex}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
