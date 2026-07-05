using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinMeters.Services;

/// <summary>
/// Owns WinMeters' positioning, docking to the shell taskbar, and float-mode
/// freedom. Faithful port of kil0bit System Monitor's <c>OverlayWindow</c>
/// positioning strategy — the only deviations are WPF plumbing (we own a
/// managed <see cref="Window"/> instead of a raw <c>CreateWindowEx</c> HWND,
/// so we route Y writes through <see cref="Window.Top"/> when the shell is
/// not involved and through <c>SetWindowPos</c> when it is).
/// <list type="number">
///   <item>
///     <b>WM_WINDOWPOSCHANGING + StickToTaskbar</b>: read <c>Shell_TrayWnd</c>'s rect,
///     compute <c>cy = tb.Top + (tb.H - oh) / 2</c> where oh follows
///     kil0bit's <c>(ShowPods ? 36 : 32) × DpiScale × ScaleFactor</c> formula,
///     and overwrite <c>WINDOWPOS.y</c> in-place. The shell sees the
///     corrected Y before the move is committed.
///   </item>
///   <item>
///     <b>WM_EXITSIZEMOVE</b>: <c>GetWindowRect</c> the bar and persist
///     <see cref="AppSettings.WindowSettings.PositionX"/> / <c>PositionY</c>
///     verbatim. Matches kil0bit's <c>WM_EXITSIZEMOVE</c> branch in
///     <c>OverlayWindow.WndProc</c>.
///   </item>
///   <item>
///     <b>WM_DPICHANGED / WM_DISPLAYCHANGE / WM_SETTINGCHANGE</b>: refresh the
///     cached DPI and re-run the alignment formula via
///     <see cref="AlignToTaskbarCenterPx"/>.
///   </item>
///   <item>
///     <b>WM_APPBAR_CALLBACK</b>: same handler as legacy WinMeters — mirror
///     the shell's fullscreen-app notification into
///     <see cref="Window.Visibility"/>.
///   </item>
///   <item>
///     <b>AttachToTaskbar / FreeFloat / ReAttach</b>: <c>FindWindow</c> +
///     <c>SetWindowLongPtr(GWL_HWNDPARENT, taskbar)</c> + <c>ABM_NEW</c> /
///     <c>ABM_REMOVE</c>. Keep <see cref="IsRegistered"/> so MainWindow can
///     short-circuit re-attach on monitor changes.
///   </item>
/// </list>
/// </summary>
internal sealed class AppBarService : IDisposable
{
    private readonly Window _window;
    private AppSettings _settings;

    private IntPtr _hwnd;
    private IntPtr _taskbarHwnd;
    private uint _currentDpi = 96;
    private float _dpiScale = 1.0f;
    /// <summary>
    /// Per-monitor DPI scale (96 = 100 %, 144 = 150 %, 192 = 200 %).
    /// Updated from <c>WM_DPICHANGED</c> via <see cref="NativeMethods.GetDpiForWindow"/>.
    /// Read by <c>MainWindow</c> when sizing pie-chart bitmaps (see
    /// <c>PieChartRenderer.UpdatePieWithCache</c>) so the rendered pixel grid
    /// matches the user's monitor scaling.
    /// </summary>
    public float DpiScale => _dpiScale;
    private bool _registered;
    private bool _disposed;

    /// <summary>Logical bar height (DIPs). WinMeters uses 40 (its XAML StackPanel
    /// intrinsic content height with Margin="5,5,0,5" on CpuContainer + Margin="0,5"
    /// on the 2-row panels); kil0bit uses 32 normal / 36 with pods. Bumped from
    /// 32 → 40 in the centring fix so the formula's anchor matches WinMeters'
    /// actual rendered height and the WM_WINDOWPOSCHANGING Y-centre lands at the
    /// visual centre of the WPF window (was drifting ~4-12 DIPs downward before
    /// this restatement).</summary>
    private const int BarHeightNormalDips = 40;
    private const int BarHeightPodsDips = 36;

    public bool IsRegistered => _registered;

    public AppBarService(Window window, AppSettings settings)
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

    /// <summary>True when the bar is glued to the taskbar (kil0bit semantics).</summary>
    public bool IsTaskbarStuck => _settings.Window.StickToTaskbar;

    /// <summary>
    /// Mixture of kil0bit's <c>OverlayWindow.AttachToTaskbar</c> plus
    /// WinMeters' required registration with <see cref="NativeMethods.SHAppBarMessage"/>.
    /// Idempotent: returns <c>true</c> immediately if already attached.
    /// </summary>
    public bool AttachToTaskbar()
    {
        if (_disposed) return false;

        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            WinMeters.Log.D("AppBarService.AttachToTaskbar: HWND not ready; skipped.");
            return false;
        }

        try
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero)
            {
                WinMeters.Log.D("AppBarService.AttachToTaskbar: Shell_TrayWnd not found.");
                return false;
            }

            _taskbarHwnd = taskbar;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, taskbar);

            // Re-assert TOPMOST so the bar draws above the system taskbar (also topmost
            // in Windows 10+). Matches kil0bit's WS_EX_TOPMOST extended style.
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            _currentDpi = NativeMethods.GetDpiForWindow(_hwnd);
            if (_currentDpi == 0) _currentDpi = 96;
            _dpiScale = _currentDpi / 96.0f;

            var data = new NativeMethods.APPBARDATA
            {
                cbSize           = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd             = _hwnd,
                uCallbackMessage = NativeMethods.WM_APPBAR_CALLBACK,
                uEdge            = NativeMethods.ABE_BOTTOM,
                rc               = default,
                lParam           = IntPtr.Zero,
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
            _registered = true;

            // Disable DWM transition animations on our HWND so moving the
            // bar above/below the system taskbar no longer re-triggers a
            // DWM composition pass on the surrounding taskbar surface
            // (the user-visible symptom is the taskbar fading / changing
            // opacity when WinMeters is dragged across it). kil0bit calls
            // the same `DwmSetWindowAttribute(_hWnd, 3, ref 1, sizeof(int))`
            // in its OverlayWindow constructor with attribute 3 =
            // DWMWA_TRANSITIONS_FORCEDISABLED. Best-effort: a non-zero
            // HRESULT (older shell, dwmapi missing, etc.) is logged but
            // does not fail the attach.
            int disableTransitions = 1;
            int hr = NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref disableTransitions,
                sizeof(int));
            if (hr != 0)
            {
                WinMeters.Log.D(
                    $"AppBarService.AttachToTaskbar: DwmSetWindowAttribute(DWMWA_TRANSITIONS_FORCEDISABLED) returned HRESULT=0x{hr:X8} (best-effort, ignored).");
            }
            else
            {
                WinMeters.Log.D("AppBarService.AttachToTaskbar: DWM transitions disabled on this HWND.");
            }

            WinMeters.Log.D(
                $"AppBarService: attached (hwnd=0x{_hwnd:X}, taskbar=0x{_taskbarHwnd:X}, dpi={_currentDpi}).");

            AlignToTaskbarCenterPx();
            return true;
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"AppBarService.AttachToTaskbar failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Detach from the shell taskbar; pair-kil0bit's <c>SetWindowLongPtr(NULL)</c>
    /// + <c>SHAppBarMessage(ABM_REMOVE)</c>. Idempotent.
    /// </summary>
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
                    hWnd   = _hwnd,
                    lParam = IntPtr.Zero,
                };
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
                _registered = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, IntPtr.Zero);
            }

            _taskbarHwnd = IntPtr.Zero;

            WinMeters.Log.D("AppBarService: detached (float mode).");
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"AppBarService.FreeFloat failed: {ex}");
        }
    }

    /// <summary>Tear down and re-register. Used when MonitorIndex changes / DPI shifts.</summary>
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
        try { FreeFloat(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Sends the WPF window into stick mode (parent to Shell_TrayWnd, register
    /// appbar) when <see cref="AppSettings.WindowSettings.StickToTaskbar"/> is
    /// <c>true</c>; otherwise ensures we are detached and applies the saved X/Y
    /// back to the HWND via <see cref="Window.Left"/>/<see cref="Window.Top"/>.
    /// </summary>
    public void ApplyIntegrationState(double savedXDip, double savedYDip)
    {
        if (_disposed) return;

        if (IsTaskbarStuck)
        {
            // Adapt the WPF Window's Top so the (re)align math has a sane input.
            // AttachToTaskbar's internal AlignToTaskbarCenterPx() will overwrite Top
            // again at the end with the taskbar-centred value.
            _window.Left = savedXDip;
            if (!_registered)
            {
                AttachToTaskbar();
                return;
            }

            // Already registered — just re-align so the saved X takes effect.
            AlignToTaskbarCenterPx();
            return;
        }

        // Float mode: detach if we were an appbar, then position the WPF window from
        // saved X/Y. WPF routes through Window.Left/Top so DPI conversion is automatic.
        FreeFloat();
        _window.Left = savedXDip;
        _window.Top  = savedYDip;
    }

    /// <summary>
    /// WndProc hook — installed via <c>HwndSource.AddHook</c> in MainWindow. Handles
    /// the kil0bit-style positioning messages so the WM_WINDOWPOSCHANGING Y-clamp
    /// is the single source of truth for taskbar centring, and the saved X/Y stays
    /// in sync with whatever the user dragged the bar to.
    /// </summary>
    public IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        _hwnd = hwnd;

        // 1. WM_WINDOWPOSCHANGING — rewrite the Y in the WINDOWPOS the shell is about
        //    to commit. kil0bit does this verbatim; we do the same so we clamp exactly
        //    to the live taskbar rect.
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && IsTaskbarStuck && lParam != IntPtr.Zero)
        {
            try
            {
                var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                if (ClampYToTaskbarPx(ref pos))
                    Marshal.StructureToPtr(pos, lParam, false);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"AppBarService.HwndHook WM_WINDOWPOSCHANGING: {ex.Message}");
            }
            return IntPtr.Zero;
        }

        // 2. WM_WINDOWPOSCHANGED — if we are still registered as an appbar, tell the
        //    shell our position changed so other appbars can re-arrange.
        if (msg == NativeMethods.WM_WINDOWPOSCHANGED)
        {
            if (_registered && _hwnd != IntPtr.Zero)
            {
                var data = new NativeMethods.APPBARDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                    hWnd   = _hwnd,
                    lParam = IntPtr.Zero,
                };
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_WINDOWPOSCHANGED, ref data);
            }
            return IntPtr.Zero;
        }

        // 3. WM_EXITSIZEMOVE — kil0bit persists the post-drag X/Y here. We do the
        //    same: read the live HWND rect and store it in AppSettings so the user's
        //    free-floating preference survives a restart.
        if (msg == NativeMethods.WM_EXITSIZEMOVE)
        {
            PersistCurrentPositionPx();
            return IntPtr.Zero;
        }

        // 4. DPI / display-change / setting-change — refresh DPI cache and re-align.
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

        // 5. WM_APPBAR_CALLBACK — same ABN_* handling as legacy WinMeters, kept so
        //    fullscreen-app behaviour matches the prior implementation.
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
                {
                    // Kil0bit-style "Hide in Fullscreen" toggle. Mirrors
                    // _config.Config.HideOnFullscreen — when the flag is off the
                    // bar stays visible during fullscreen sessions (the user wants
                    // their meter always available). When on, fullscreen apps
                    // collaboratively hide us via the shell, kil0bit semantics.
                    //
                    // Float-mode fullscreen detection is intentionally NOT wired
                    // here: ABN_FULLSCREENAPP is a shell-side notification that
                    // fires only for registered appbars. Adding float-mode
                    // detection would require a separate WM_ACTIVATEAPP hook +
                    // GetMonitorInfo comparison in MainWindow. If the user enables
                    // HideInFullscreen while floating, the bar stays visible
                    // (documented in the menu's tooltip if added later).
                    if (!_settings.General.HideInFullscreen) break;

                    bool fullscreen = lParam.ToInt64() != 0;
                    if (fullscreen)
                    {
                        _window.Visibility = Visibility.Collapsed;
                    }
                    else if (!_settings.Window.IsHiddenByUser)
                    {
                        _window.Visibility = Visibility.Visible;
                    }
                    break;
                }

                case NativeMethods.ABN_STATECHANGE:
                    break;
            }
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Reads the live HWND rect and stores the X/Y in <see cref="AppSettings"/>.
    /// Saved values are DIPs (the same coordinate space as <see cref="Window.Left"/>
    /// / <see cref="Window.Top"/>), so we divide by the current DPI scale before
    /// persisting; otherwise a 150% DPI session would drift the bar ~1.5× its
    /// actual pixel position on every restart.
    /// </summary>
    private void PersistCurrentPositionPx()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r) == 0) return;

        // Refresh DPI cache so the conversion matches what the user is currently
        // looking at (Windows allows per-monitor DPI which can shift between
        // sessions if the user dragged monitors).
        uint currentDpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (currentDpi == 0) currentDpi = 96;
        double dpiScale = currentDpi / 96.0;
        if (dpiScale <= 0) dpiScale = 1.0;

        _settings.Window.PositionX = r.Left / dpiScale;
        _settings.Window.PositionY = r.Top  / dpiScale;
    }

    /// <summary>
    /// Modifies <paramref name="pos"/> so <c>pos.y</c> lands at the taskbar's
    /// vertical centre. Returns <c>true</c> when a change was made.
    /// <para>
    /// The window-height anchor is the IDEAL bar height from
    /// <see cref="ComputeBarHeightPx"/> — kil0bit's fixed
    /// <c>oh = (ShowPods ? 36 : 32) × _dpiScale × ScaleFactor</c> formula
    /// restated for WinMeters' actual 40-DIP content height (BarHeightNormalDips
    /// was bumped from 32 to 40 to match WinMeters' XAML intrinsic height;
    /// previously 32 didn't match and the bar drifted ~4-12 DIPs downward).
    /// The companion fix on the WPF side (<see cref="MainWindow.ApplyScale"/> +
    /// <see cref="MainWindow"/> xaml's <c>SizeToContent="Width"</c> +
    /// <c>Height="40"</c>) locks the WPF window's actual DIP-height to
    /// <c>BarHeightNormalDips × ScaleFactor = 40 × Scale</c>, so the WPF window's
    /// pixel-height always equals <c>winHPx</c> and the centred Y lands at the
    /// visual centre of the WPF window. Using <c>pos.cy</c> or the live HWND
    /// rect was tried before and oscillated with content edits; locking
    /// <c>this.Height</c> from the scaling code is the cleaner restatement of
    /// the kil0bit invariant.
    /// </para>
    /// </summary>
    private bool ClampYToTaskbarPx(ref NativeMethods.WINDOWPOS pos)
    {
        if (!TryGetTaskbarRect(out NativeMethods.RECT tb, out _)) return false;

        // Mirror kil0bit's `OverlayWindow.WndProc` WM_WINDOWPOSCHANGING
        // branch: the centring math always uses the IDEAL bar height
        // (`oh = 32 × dpiScale × ScaleFactor`), never the shell's
        // committed `pos.cy`. WPF's `Height="40"` (and SizeToContent-driven
        // reflows) routinely produce a live rect taller than the kil0bit
        // fixed 32-DIP value, so using the live height clipped the bar
        // flush with the taskbar top.
        int winHPx = ComputeBarHeightPx();

        int cyPx = ComputeCenteredY(tb, winHPx);

        if (pos.y == cyPx) return false;
        pos.y = cyPx;
        return true;
    }

    /// <summary>
    /// Resolves the live <c>Shell_TrayWnd</c> rect and HWND in one pass,
    /// returning <c>false</c> if the shell's taskbar cannot be located
    /// (Explorer restart, headless Desktop, etc.). Centralised so
    /// <see cref="ClampYToTaskbarPx"/> and <see cref="AlignToTaskbarCenterPx"/>
    /// share the lookup instead of calling <c>FindWindow</c> twice per align.
    /// </summary>
    private static bool TryGetTaskbarRect(out NativeMethods.RECT rect, out IntPtr hwnd)
    {
        rect = default;
        hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (hwnd == IntPtr.Zero) return false;
        return NativeMethods.GetWindowRect(hwnd, out rect) != 0;
    }

    /// <summary>
    /// Returns the Y coordinate that vertically centres a window of height
    /// <paramref name="winHPx"/> inside the given taskbar <paramref name="tb"/>,
    /// clamped so the window never overhangs either taskbar edge. Shared
    /// between <see cref="ClampYToTaskbarPx"/> (live per-move clamp) and
    /// <see cref="AlignToTaskbarCenterPx"/> (initial / DPI / display-change
    /// SetWindowPos) so the two can never drift apart.
    /// </summary>
    private static int ComputeCenteredY(NativeMethods.RECT tb, int winHPx)
    {
        if (winHPx <= 0)
        {
            // Surface the upstream defect (e.g. a GetWindowRect returning a zero
            // rect) rather than silently substitute a 1-px value that would
            // render the bar misleadingly close to "centred".
            WinMeters.Log.D(
                $"ComputeCenteredY: invalid windowHeight={winHPx}; snapping to taskbar top.");
            return tb.Top;
        }
        int tbH = tb.Bottom - tb.Top;
        int cyPx = tb.Top + (tbH - winHPx) / 2;

        // Never let the window overhang the taskbar's bottom edge, even when
        // the window is taller than the taskbar (autohide collapse, small
        // taskbar on a low-resolution monitor, etc.).
        if (cyPx + winHPx > tb.Bottom) cyPx = tb.Bottom - winHPx;
        if (cyPx < tb.Top) cyPx = tb.Top;
        return cyPx;
    }

    /// <summary>
    /// Computes the (post-dpi-and-scale) bar height in physical pixels using
    /// kil0bit's <c>(ShowPods ? 36 : 32) × dpiScale × ScaleFactor</c> formula
    /// restated for WinMeters' 40-DIP XAML intrinsic content
    /// (<see cref="BarHeightNormalDips"/>). WinMeters has no ShowPods setting,
    /// so we always use 40.
    /// </summary>
    private int ComputeBarHeightPx()
    {
        double scaleFactor = Math.Max(0.25, _settings.General.Scale > 0 ? _settings.General.Scale : 1.0);
        return (int)Math.Round(BarHeightNormalDips * (double)_dpiScale * scaleFactor);
    }

    /// <summary>
    /// Re-position the bar centred inside the taskbar (Y axis only — kil0bit's
    /// <c>AlignToTaskbarCenter</c> semantics: <c>cy = tb.Top + (tb.H - oh) / 2</c>,
    /// X taken from the live HWND's Left edge so the user can subsequently drag
    /// horizontally via <c>WM_EXITSIZEMOVE</c>). Called from AttachToTaskbar
    /// (initial) and from DPI/display/setting-change handlers (refresh).
    /// <para>
    /// The window-height anchor for Y-centring is the IDEAL bar height
    /// from <see cref="ComputeBarHeightPx"/> — kil0bit's fixed
    /// <c>oh = (ShowPods ? 36 : 32) × _dpiScale × ScaleFactor</c> formula.
    /// Using the live rect height instead drifted the bar when Scale ≠ 1.0.
    /// </para>
    /// </summary>
    private void AlignToTaskbarCenterPx()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!TryGetTaskbarRect(out NativeMethods.RECT tb, out IntPtr taskbar)) return;

        int winHPx = ComputeBarHeightPx();
        int cyPx = ComputeCenteredY(tb, winHPx);

        // X is taken verbatim from the live HWND's Left edge once a rect is
        // available (matches kil0bit's behaviour: only the Y axis is re-
        // anchored, the bar preserves whatever X it had at attach/dock time).
        // Falls back to the last-saved PositionX before the first HWND rect is
        // available (degenerate attach race).
        int xPx = (int)(_settings.Window.PositionX ?? 100);
        if (NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT ourRect) != 0)
            xPx = ourRect.Left;

        // If our parent relationship drifted (Explorer restart / monitor change),
        // re-attach so the shell still treats us as part of the taskbar.
        if (taskbar != _taskbarHwnd)
        {
            _taskbarHwnd = taskbar;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_HWNDPARENT, taskbar);
        }

        // Persist the centred Y in DIPs so the next un-snap and the settings
        // dialog both see the taskbar-centred position. Mirrors kil0bit's
        // `_config.Config.Y = cy` persistence. (`PositionX` is persisted by
        // WM_EXITSIZEMOVE → PersistCurrentPositionPx once the user drags
        // horizontally; we do not pre-emptively write it here.)
        _settings.Window.PositionY = cyPx / _dpiScale;

        // route through SetWindowPos with HWND_TOPMOST so the bar stays above the
        // (also topmost) system taskbar in Win10+.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            xPx, cyPx, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
