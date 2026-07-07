using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Interop;
using Microsoft.Win32;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;
using WnForms = System.Windows.Forms;
using WinMeters.Utils;

namespace WinMeters
{
    /// <summary>
    /// Main window displaying system meters (CPU, RAM, Disk, Network).
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Monitors.MonitorManager _monitorManager;
        private Services.HotkeyService? _hotkeyService;
        private Services.WindowPlacementService _placementService = null!;
        private Monitors.HardwareMonitorService? _hardwareMonitor;
        private DispatcherTimer? _timer;
        private DispatcherTimer? _zOrderTimer;
        private AppSettings _settings = new AppSettings();

        // Use ticks for more efficient rate-limiting
        private long _lastCpuTicks;
        private long _lastRamTicks;
        private long _lastDiskTicks;
        private long _lastNetTicks;
        private long _lastCpuTempTicks;
        private long _lastGpuTempTicks;
        private long _lastGpuDedicatedTicks;
        private long _lastGpuSharedTicks;

        // Cache formatted network/disk strings
        private string _lastNetDownFormatted = "";
        private string _lastNetUpFormatted = "";
        private string _lastDiskReadFormatted = "";
        private string _lastDiskWriteFormatted = "";

        // Cache formatted time
        private long _lastTimeTicks;
        private string _lastTimeFormatted = "";

        // Cache rendered bitmap + last percentage + last DPI bucket for pie charts.
        // The bitmap is produced with GDI+ (System.Drawing.Graphics.FillPie / DrawEllipse)
        // into a WPF WriteableBitmap backbuffer and displayed inside a WPF Image; see
        // Utils/PieChartRenderer.cs and RENDERING.md for the policy. Pct + DPI bucket
        // together form the cache key (Renderer.UpdatePieWithCache) — re-rendering only
        // fires when either moves by more than its threshold, so a stable meter with
        // occasional small fluctuations doesn't churn allocations.
        private WpfBitmapSource? _lastRamPieSource;
        private double _lastRamPercentage = -1;
        private int _lastRamPieDpiBucket = -1;
        private WpfBitmapSource? _lastGpuDedicatedSource;
        private double _lastGpuDedicatedPercentage = -1;
        private int _lastGpuDedicatedPieDpiBucket = -1;
        private WpfBitmapSource? _lastGpuSharedSource;
        private double _lastGpuSharedPercentage = -1;
        private int _lastGpuSharedPieDpiBucket = -1;

        // Foreground window tracking for Alt+Tab lowering mechanism
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;

        // AppBar registration so the shell treats us as part of the taskbar surface and
        // the work area is shrunk to exclude us. Eliminates the WS_EX_TOPMOST family of
        // z-order bugs (taskbar clicks, tooltip / context-menu vs bar, fullscreen apps).
        // Constructed unconditionally in OnSourceInitialized so the WndProc hook is
        // wired before any WM message arrives; Register/Unregister is toggled
        // dynamically by ApplyWindowMode based on _settings.Window.WindowMode.
        private Services.AppBarService _appBarService = null!;

        // System tray icon. WinMeters runs as a transparent overlay on the
        // taskbar (MainWindow.xaml: ShowInTaskbar=False by design) so the user
        // has no other persistent UI surface to find it in. The tray icon gives
        // them a visible "WinMeters is running" affordance plus an always-on
        // path to open Settings and Quit, which closes the "doesn't start /
        // not showing in Task Manager" complaint path: even if the bar is
        // crashed or hidden, the tray stays there. See InitializeTrayIcon()
        // for wiring + lifetime.
        private WnForms.NotifyIcon? _trayIcon;

        // Active Settings dialog. Cached so repeated clicks on the RMB-menu
        // Settings item (or the tray Show Settings entry) re-activate the
        // existing window instead of stacking a second copy. Modeless Show()
        // keeps the WPF owner window interactive so DragMove() and
        // MouseLeftButtonDown fire on the bar even while Settings is up,
        // which is the user's explicit request: "able to reposition main
        // window when settings window is open". Cleared by the Closed
        // subscriber attached inside MenuItem_Settings_Click.
        private SettingsWindow? _existingSettingsWindow;

        public MainWindow()
        {
            InitializeComponent();
            _monitorManager = new Monitors.MonitorManager();

            _settings = AppSettings.Load();
            // Construct the placement service once, after Load, so it picks up the real settings.
            _placementService = new Services.WindowPlacementService(this, _settings);
            InitializeHardwareMonitor();
            ApplySettingsInternal();
            _settings.Save();

            InitializeTrayIcon();

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
            this.Deactivated += MainWindow_Deactivated;
        }

        /// <summary>
        /// Builds the <see cref="WnForms.NotifyIcon"/> that lives in the
        /// system tray for the entire lifetime of the process. Menu is kept
        /// minimal on purpose: Show Settings (also wired to left-double-click
        /// to match Windows tray conventions), Show / Hide Bar (delegates to
        /// the same ToggleVisibility the global hotkey uses), and Quit. The
        /// Quit handler defers to MenuItem_Exit_Click so cleanup logic stays
        /// in exactly one place.
        /// All click handlers marshal onto the WPF Dispatcher because
        /// NotifyIcon's events fire on a WinForms MessageOnlyWindow thread,
        /// not on the WPF UI thread.
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                _trayIcon = new WnForms.NotifyIcon
                {
                    // System.Drawing.SystemIcons is a property of the
                    // System.Drawing namespace, not System.Windows.Forms --
                    // so it lives outside the WnForms alias on purpose.
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = "WinMeters",
                    Visible = true,
                };
                _trayIcon.ContextMenuStrip = BuildTrayMenu();

                _trayIcon.MouseDoubleClick += (_, args) =>
                {
                    if (args.Button == WnForms.MouseButtons.Left)
                    {
                        Dispatcher.Invoke(() =>
                            MenuItem_Settings_Click(this, new RoutedEventArgs()));
                    }
                };
            }
            catch (Exception ex)
            {
                // Best-effort: tray is optional. If it fails (e.g. headless
                // service environment with no shell), WinMeters still works;
                // the bar + global hotkey are unaffected.
                WinMeters.Log.D($"InitializeTrayIcon failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds (or rebuilds) the tray ContextMenuStrip. Single source of
        /// truth = <c>_settings.Window.IsHiddenByUser</c> -- the trailing
        /// ternary above flips the toggle item's label between
        /// "Hide Bar" (when the bar is visible -- so clicking will hide it)
        /// and "Show Bar" (when the bar is hidden -- so clicking will show
        /// it), and the Checked mark restamps from the same field. The
        /// whole menu is rebuilt rather than just mutating .Text on the
        /// existing item so the user sees the label change BEFORE the
        /// next right-click on the tray icon.
        /// </summary>
        private WnForms.ContextMenuStrip BuildTrayMenu()
        {
            bool isBarVisible = !_settings.Window.IsHiddenByUser;
            var menu = new WnForms.ContextMenuStrip();

            var settingsItem = new WnForms.ToolStripMenuItem("Show Settings");
            settingsItem.Click += (_, _) => Dispatcher.Invoke(() =>
                MenuItem_Settings_Click(this, new RoutedEventArgs()));

            var toggleItem = new WnForms.ToolStripMenuItem(
                isBarVisible ? "Hide Bar" : "Show Bar")
            {
                Checked = isBarVisible,
            };
            toggleItem.Click += (_, _) => Dispatcher.Invoke(() => ToggleVisibility());

            var quitItem = new WnForms.ToolStripMenuItem("Quit");
            quitItem.Click += (_, _) => Dispatcher.Invoke(() =>
                MenuItem_Exit_Click(this, new RoutedEventArgs()));

            menu.Items.Add(settingsItem);
            menu.Items.Add(toggleItem);
            menu.Items.Add(new WnForms.ToolStripSeparator());
            menu.Items.Add(quitItem);

            return menu;
        }

    private void InitializeHardwareMonitor()
    {
        // Idempotent: called from ctor once at launch. ApplyHardwareMonitor does the same
        // up/down from then on as the user toggles the Enable Hardware Monitor checkbox
        // in SettingsWindow and re-saves -- the ctor call here just covers the launch-time
        // case so a first-launch user with EnableHardwareMonitor=true gets sensors wired up
        // BEFORE their first Timer_Tick.
        if (_hardwareMonitor is not null) return;
        if (!_settings.General.EnableHardwareMonitor) return;

        try
        {
            _hardwareMonitor = new Monitors.HardwareMonitorService(
                enableCpu: true,
                enableGpu: true,
                enableMotherboard: true);
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"Failed to initialize hardware monitor: {ex.Message}");
        }
    }

    /// <summary>
    /// Bring the HardwareMonitorService up or down in lock-step with
    /// <c>_settings.General.EnableHardwareMonitor</c>. Called whenever settings change
    /// (ApplySettings / ApplySettingsLive) so flipping the Enable Hardware Monitor
    /// checkbox in SettingsWindow takes effect immediately on dialog close rather
    /// than requiring a manual app restart. The dispose branch nulls the field so
    /// MainWindow_Closed's <c>_hardwareMonitor?.Dispose()</c> becomes a safe no-op.
    /// </summary>
    private void ApplyHardwareMonitor()
    {
        if (_settings.General.EnableHardwareMonitor)
        {
            // Reuse InitializeHardwareMonitor's create branch (idempotent via the
            // _hardwareMonitor is not null guard) -- single source of truth for
            // the service constructor.
            InitializeHardwareMonitor();
        }
        else if (_hardwareMonitor is not null)
        {
            _hardwareMonitor.Dispose();
            _hardwareMonitor = null;
        }
    }

        #region Initialization & Loading

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // The legacy InitializeHeight() helper (which sized the window to the
            // system taskbar's height on first launch) is gone in the WinMeters-style
            // rewrite — the bar height is now derived dynamically from DPI + ScaleFactor
            // inside AppBarService.ComputeBarHeightPx(). Nothing to do here.
            SetupCpuBars();
            UpdateTooltips();

            // Log window state for debugging
            WinMeters.Log.D($"[WinMeters] Window loaded: Visibility={this.Visibility}, Opacity={this.Opacity}, Width={this.Width}, Height={this.ActualHeight}, Left={this.Left}, Top={this.Top}");
            WinMeters.Log.D($"[WinMeters] IsHiddenByUser={_settings.Window.IsHiddenByUser}, StickToTaskbar={_settings.Window.StickToTaskbar}");

            // Visibility from user toggle. The AppBar's ABN_FULLSCREENAPP handler will
            // also hide us while a fullscreen app is on the desktop in AppBar mode.
            if (_settings.Window.IsHiddenByUser)
            {
                this.Visibility = Visibility.Collapsed;
                WinMeters.Log.D("[WinMeters] Window hidden due to IsHiddenByUser=true");
            }
            else
            {
                this.Visibility = Visibility.Visible;
            }

            // Note: floating-mode position restore happens upstream in
            // OnSourceInitialized → ApplyWindowMode → AppBarService.AlignToTaskbarCenter
            // (which already chose between RestorePosition and ClampToTargetMonitor).
            // Re-running RestorePosition here would double-set the window position.
        }

        #endregion

        #region CPU Bar Setup

        private sealed class CpuBarSet
        {
            public System.Windows.Shapes.Rectangle? RectSystem;
            public System.Windows.Shapes.Rectangle? RectUser;
        }
        private readonly List<CpuBarSet> _cpuBarSets = new();

        private void SetupCpuBars()
        {
            CpuContainer.Children.Clear();
            _cpuBarSets.Clear();

            int logicalCores = _monitorManager.LogicalCoreCount;
            int barsToShow = _settings.General.CombineLogicalCores
                ? ((logicalCores > 1) ? logicalCores / 2 : 1)
                : logicalCores;
            if (barsToShow < 1) barsToShow = 1;

            double borderThickness = _settings.Colors.CpuBorderThickness;

            for (int i = 0; i < barsToShow; i++)
            {
                var hostGrid = new Grid
                {
                    Width = Constants.Display.CpuBarWidth,
                    Margin = new Thickness(Constants.Display.CpuBarMargin, 0, Constants.Display.CpuBarMargin, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = Constants.Display.CpuBarHeight
                };

                var border = new Border
                {
                    BorderBrush = WpfBrushes.Black,
                    BorderThickness = new Thickness(borderThickness),
                    SnapsToDevicePixels = true
                };

                var innerGrid = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
                innerGrid.Children.Add(new WpfRectangle { Fill = WpfBrushes.Transparent });

                var rectSys = new System.Windows.Shapes.Rectangle
                {
                    Fill = ColorHelper.ParseBrush(_settings.Colors.CpuSys),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = 0
                };
                innerGrid.Children.Add(rectSys);

                var rectUser = new System.Windows.Shapes.Rectangle
                {
                    Fill = ColorHelper.ParseBrush(_settings.Colors.CpuUser),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = 0
                };
                innerGrid.Children.Add(rectUser);

                border.Child = innerGrid;
                hostGrid.Children.Add(border);
                CpuContainer.Children.Add(hostGrid);
                _cpuBarSets.Add(new CpuBarSet { RectSystem = rectSys, RectUser = rectUser });
            }
        }

        #endregion

        #region Window Interaction

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_settings.Window.LockPosition)
            {
                this.DragMove();
            }
        }

        private void SavePosition()
        {
            // Persist the WPF Window's current DIP coordinates so the next launch
            // lands at the same screen-position. WPF dep-property source of truth,
            // not the raw HWND rect.
            _placementService.SaveCurrentDips();
            _settings.Save();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                // Tear down the tray icon FIRST so it disappears the moment
                // the user sees the bar close — keeps the visual contract
                // intact (tray = running process) and removes the misleading
                // "Quit didn't work" affordance on slow widget disposes below.
                // Both the currently-attached ContextMenuStrip AND the
                // NotifyIcon must be disposed: the strip holds a native
                // menu HWND the icon does not own-transitively, and while
                // every toggle in ToggleVisibility already disposes the
                // obsolete strip via oldMenu?.Dispose(), the most recently
                // rebuilt strip stays attached to the icon right up to
                // shutdown. Without this line the strip leaks its native
                // handle on every WinMeters exit.
                _trayIcon?.ContextMenuStrip?.Dispose();
                _trayIcon?.Dispose();
                _trayIcon = null;

                _hotkeyService?.Dispose();
                _timer?.Stop();
                _zOrderTimer?.Stop();
                _appBarService?.Dispose();
                _monitorManager?.Dispose();
                _hardwareMonitor?.Dispose();
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"MainWindow_Closed: {ex}");
            }
        }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Cold-open path: opt THIS PROCESS into dark mode before any
        // future syscolor-derived brush read inside MainWindow. Mirrors
        // the same call in SettingsWindow.ctor; the bar's popup-time
        // ApplyMenuChromeMode re-applies the right value immediately
        // before TrackPopupMenuEx anyway, so this cold-open call
        // doesn't fight any popup-time reset. See Services.ThemeService
        // for the Win10 1903 per-process uxtheme quirk that drives this.
        Services.ThemeService.InitializeDarkMode();

        var helper = new WindowInteropHelper(this);
            var source = HwndSource.FromHwnd(helper.Handle);
            if (source is null) return;

            // System-wide hotkey is owned by HotkeyService; we just install the hook and
            // tell the service to register itself.
            _hotkeyService = new Services.HotkeyService(helper.Handle, ToggleVisibility);
            source.AddHook(_hotkeyService.HwndHook);
            _hotkeyService.Register();

            // Construct AppBarService unconditionally so its HwndSource hook is wired
            // BEFORE the first WM message arrives. ApplyWindowMode below toggles
            // Register/Unregister according to settings without touching hooks.
            _appBarService = new Services.AppBarService(this, _settings);
            source.AddHook(_appBarService.HwndHook);

            // WM_DISPLAYCHANGE hook so we can re-apply positioning when the user
            // unplugs / wakes / plugs a monitor. Single source of truth — AppBarService's
            // own HwndHook deliberately does NOT react to WM_DISPLAYCHANGE.
            source.AddHook(MonitorChangeHook);

            // WM_RBUTTONUP hook for the native HMENU-based popup menu. Fires when
            // the user right-clicks anywhere on the bar; replaces the WPF ContextMenu
            // (removed from MainWindow.xaml in this commit) with a native Win32
            // HMENU driven by CreatePopupMenu / AppendMenu / TrackPopupMenuEx, matching
            // .Kilobit/OverlayWindow.cs WndProc. The menu is drawn by the OS, forced
            // dark via uxtheme calls; the WPF handler simply consumes WM_RBUTTONUP
            // and returns IntPtr.Zero.
            source.AddHook(WmRButtonUp);

            // Activate the mode requested by settings.
            ApplyWindowMode();
        }

        /// <summary>
        /// Installs/refreshes the integration mode chosen in settings. Idempotent; safe
        /// to call on app boot and again whenever the user changes WindowMode or
        /// MonitorIndex via the settings dialog.
        /// </summary>
        public void ApplyWindowMode()
        {
            // WinMeters-style split: StickToTaskbar=true -> shell owns positioning and z-order;
            // otherwise we treat the bar as a free-floating window whose X/Y come straight
            // from settings (verbatically, with a one-time SetWindowPos on first launch to
            // sit above normal windows before the 500ms keepalive kicks in).
            double savedXDip = _placementService.GetX();
            double savedYDip = _placementService.GetY();

            if (_settings.Window.StickToTaskbar)
            {
                // Stuck mode: shell owns positioning once attached. Apply the user's
                // KeepOnTop preference LAST so it overrides the unconditional
                // Topmost=true that follows (the shell keeps us above the taskbar
                // regardless of Z-order anyway; Topmost is a backup).
                _appBarService.ApplyIntegrationState(savedXDip, savedYDip);
                ApplyKeepOnTop(_settings.General.KeepOnTop);
                WinMeters.Log.D("MainWindow: Stuck-to-taskbar mode active.");
            }
            else
            {
                // Float mode: detach (or stay detached) and put the window back at
                // the saved X/Y. One-time HWND_TOPMOST placement is folded into
                // ApplyKeepOnTop so it fires only when the user actually wants
                // keep-on-top; otherwise we land at HWND_NOTOPMOST.
                _appBarService.ApplyIntegrationState(savedXDip, savedYDip);
                ApplyKeepOnTop(_settings.General.KeepOnTop);
                WinMeters.Log.D("MainWindow: Float mode active.");
            }
        }

        private void StartZOrderTimer()
        {
            if (_zOrderTimer is null)
            {
                _zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _zOrderTimer.Tick += EnforceZOrder;
            }
            _zOrderTimer.Start();
        }

        private void StopZOrderTimer() => _zOrderTimer?.Stop();

        /// <summary>
        /// WinMeters's <c>_config.Config.AlwaysOnTop</c> toggle semantics. When
        /// <paramref name="keepOnTop"/> is <c>true</c>: install WPF Topmost=true
        /// and start the EnforceZOrder timer (so we keep re-asserting
        /// HWND_TOPMOST in floating mode). When <c>false</c>: stop the timer
        /// (zero CPU cost — no background re-assertion wars with other apps),
        /// demote the WPF Topmost flag, and fire a single one-time HWND_NOTOPMOST
        /// so we fall back into standard Windows Z-order immediately rather than
        /// staying stuck above normal windows from the last timer tick.
        ///
        /// In stuck-to-taskbar mode the shell owns z-order so the Topmost flag is
        /// effectively decorative; both branches are still applied so direct
        /// staging (e.g. shell pause during a debug session) lands predictably.
        /// </summary>
        private void ApplyKeepOnTop(bool keepOnTop)
        {
            this.Topmost = keepOnTop;

            if (keepOnTop)
            {
                StartZOrderTimer();
                return;
            }

            StopZOrderTimer();

            // One-shot HWND_NOTOPMOST demote so we don't keep sitting above
            // normal windows from a previous timer tick. SWP_NOMOVE / NOSIZE /
            // NOACTIVATE so we don't disrupt focus or layout.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>
        /// kil0bit's z-order enforcement. Runs every 500ms in floating mode and
        /// re-asserts HWND_TOPMOST only when the foreground window is NOT the
        /// shell's taskbar (re-asserting while the taskbar is in front causes
        /// visible blinking). Also skips the assertion when we are already the
        /// top-most window (GW_HWNDPREV == IntPtr.Zero).
        /// </summary>
        private void EnforceZOrder(object? sender, EventArgs e)
        {
            // Float mode always re-asserts TOPMOST so we stay above normal windows
            // while the user is interacting with other apps. In stuck-to-taskbar
            // mode the shell owns z-order so the timer is not started at all
            // (see StartZOrderTimer / StopZOrderTimer).
            //
            // The kil0bit-style KeepOnTop toggle gates the timer: when false we
            // have nothing to enforce, so we exit immediately and avoid waking
            // the timer thread. ApplyKeepOnTop stops the timer entirely when the
            // user disables KeepOnTop, but a late Tick could still be in flight
            // when the menu toggle fires — this gate defends against that.
            if (!_settings.General.KeepOnTop) return;
            if (_settings.Window.StickToTaskbar) return;
            if (this.Visibility != Visibility.Visible) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;

            // Skip re-asserting while the shell's taskbar is in front of us.
            var sb = new System.Text.StringBuilder(256);
            if (NativeMethods.GetClassName(fg, sb, sb.Capacity) > 0)
            {
                string fgClass = sb.ToString();
                if (fgClass == "Shell_TrayWnd" || fgClass == "Shell_SecondaryTrayWnd")
                    return;
            }

            // Only re-assert when something else is already above us in Z-order.
            IntPtr prev = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV);
            if (prev == IntPtr.Zero) return;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        /// <summary>
        /// WndProc hook dedicated to monitor-configuration changes. Fires on
        /// WM_DISPLAYCHANGE (wParam = bit-depth; lParam = resolution) when the user
        /// plugs, unplugs, or wakes a monitor. We re-resolve the saved MonitorIndex
        /// against the current Screen.AllScreens and ApplyWindowMode re-registers
        /// or re-snaps. Deliberately does NOT mutate <c>_settings.Window.MonitorIndex</c>
        /// or call <c>_settings.Save()</c> — KVM-switched or sleeping monitors should
        /// not erase the user's saved preference during a transient disconnect.
        /// </summary>
        private IntPtr MonitorChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_DISPLAYCHANGE = 0x007E
            if (msg == 0x007E)
            {
                WinMeters.Log.D("MainWindow: WM_DISPLAYCHANGE received.");
                ApplyWindowMode();
            }
            return IntPtr.Zero;
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            // In floating mode the 500ms EnforceZOrder timer handles TOPMOST
            // re-assertion (with foreground-class guard to avoid blinking on the
            // taskbar). In AppBar mode the shell owns z-order. Either way this
            // handler is a no-op — kept for backwards compatibility with XAML
            // event wiring.
        }

        private void ToggleVisibility()
        {
            bool currentlyVisible = this.Visibility == Visibility.Visible;

            if (currentlyVisible)
            {
                _settings.Window.IsHiddenByUser = true;
                this.Visibility = Visibility.Collapsed;
            }
            else
            {
                _settings.Window.IsHiddenByUser = false;
                this.Visibility = Visibility.Visible;
                // In floating mode we keep the saved X/Y verbatim; if the
                // user dragged off-screen while hidden, the next
                // AlignToTaskbarCenter (or settings reload) handles the
                // recovery. In AppBar mode the shell repositions us on
                // the next ABN_POSCHANGED.
            }
            _settings.Save();

            // Rebuild the tray ContextMenuStrip so:
            //   (1) the toggle item's label flips ("Hide Bar" -> "Show Bar"
            //       or vice-versa) to reflect the action available next,
            //   (2) the checkmark restamps from the source of truth.
            // BuildTrayMenu reads _settings.Window.IsHiddenByUser directly,
            // so the rebuild implicitly performs both updates. The old
            // ContextMenuStrip is disposed so its native handles don't
            // accumulate on every toggle -- safe because WinForms has
            // already closed the menu before this click handler runs.
            if (_trayIcon is not null)
            {
                var oldMenu = _trayIcon.ContextMenuStrip;
                _trayIcon.ContextMenuStrip = BuildTrayMenu();
                oldMenu?.Dispose();
            }
        }

        #endregion

        #region Popup Menu (WM_RBUTTONUP, native HMENU)

        // Native Win32 popup menu driven by WM_RBUTTONUP, mirroring
        // .Kilobit/OverlayWindow.cs WndProc verbatim: 10 items + 3
        // separators, command IDs 1001-1009 in the same order, MF_CHECKED
        // for the four live toggles, MF_SEPARATOR for the dividers, and
        // the menu chrome forced dark via uxtheme calls (SetPreferredAppMode
        // / AllowDarkModeForWindow / FlushMenuThemes) right before
        // TrackPopupMenuEx. Replaces the previous WPF <ContextMenu> in
        // MainWindow.xaml (and its 4 WPF MenuItem Click handlers) with a
        // single OS-drawn HMENU. The custom WinMetersMenuItemTemplate
        // ControlTemplate + MenuDivider + ItemContainerStyle were removed
        // from Themes/WinMetersTheme.xaml in the same commit as no
        // consumer remained after the WPF ContextMenu was deleted.

        /// <summary>Win32 WM_RBUTTONUP message id - fires when the user releases the right mouse button.</summary>
        private const int WM_RBUTTONUP = 0x0205;

        /// <summary>
        /// Cached <see cref="NativeMethods.MONITORINFO.cbSize"/> value. The struct is
        /// fixed-size (40 bytes on x64), so we evaluate Marshal.SizeOf once at type
        /// init instead of on every WM_RBUTTONUP. The shell reads cbSize on entry to
        /// <see cref="NativeMethods.GetMonitorInfo"/>; passing 0 makes the call fail
        /// silently with ERROR_INVALID_PARAMETER.
        /// </summary>
        private static readonly uint MonitorInfoCbSize =
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>();

        /// <summary>
        /// HwndSource hook for WM_RBUTTONUP. Builds the native HMENU,
        /// forces dark chrome via uxtheme, positions it above/below the
        /// bar based on the bar's screen quadrant, runs
        /// <see cref="NativeMethods.TrackPopupMenuEx"/> (which blocks
        /// until the user picks an item or dismisses), tears the menu
        /// down, and routes the chosen command to
        /// <see cref="DispatchMenuCommand"/>. Returns IntPtr.Zero
        /// unconditionally - WPF's default right-click -> ContextMenu
        /// behaviour has nothing to act on (we removed the WPF
        /// ContextMenu from MainWindow.xaml) so letting the message
        /// bubble costs nothing.
        /// </summary>
        private IntPtr WmRButtonUp(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_RBUTTONUP) return IntPtr.Zero;

            // Skip only when the bar is Collapsed (no hit-testing; can't
            // receive WM_RBUTTONUP anyway). Visibility.Hidden still
            // hit-tests, so the user can still right-click an invisible
            // bar (rare, but possible if a future "peek" mode sets
            // Hidden). The previous `!= Visible` gate was too aggressive:
            // it would silently eat right-clicks on a Hidden bar and
            // would also reject the transient states WPF goes through
            // during Opacity animations.
            if (this.Visibility == Visibility.Collapsed) return IntPtr.Zero;

            // Get cursor position in virtual-screen pixels (Win32
            // convention, same coordinate space as GetWindowRect / MONITORINFO).
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
        /// .Kilobit/OverlayWindow.cs WM_RBUTTONUP. The four live
        /// toggles are appended with <c>MF_CHECKED | MF_STRING</c>
        /// (or just <c>MF_STRING</c> when off) so the user sees the
        /// current state of each toggle directly in the menu chrome
        /// - the kil0bit reference draws a checkmark next to enabled
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
        /// <see cref="NativeMethods.TrackPopupMenuEx"/> to the
        /// equivalent action. Mirrors the kil0bit switch (cmd
        /// 1001 = Settings, 1002 = Task Manager, 1003 = About ->
        /// Settings, 1004 = Exit, 1006 = Lock toggle, 1007 = Snap
        /// toggle, 1008 = Keep-on-top toggle, 1009 = Hide-in-fullscreen
        /// toggle). Each toggle calls a self-contained Toggle* helper
        /// so the toggle state, the side-effect, and the persist all
        /// live in one place - no leftover WPF MenuItem.IsChecked
        /// round-trips.
        /// </summary>
        private void DispatchMenuCommand(uint cmd)
        {
            switch (cmd)
            {
                case NativeMethods.IDM_SETTINGS:
                    OpenSettingsAndNavigateTo(null);
                    break;

                case NativeMethods.IDM_TASKMGR:
                    LaunchTaskManager();
                    break;

                case NativeMethods.IDM_ABOUT:
                    // kil0bit parity: cmd 1003 opens Settings and
                    // auto-navigates to the About section.
                    OpenSettingsAndNavigateTo("About");
                    break;

                case NativeMethods.IDM_EXIT:
                    MenuItem_Exit_Click(this, new RoutedEventArgs());
                    break;

                case NativeMethods.IDM_LOCK:
                    ToggleLockPosition();
                    break;

                case NativeMethods.IDM_SNAP:
                    ToggleSnapToTaskbar();
                    break;

                case NativeMethods.IDM_KEEPONTOP:
                    ToggleKeepOnTop();
                    break;

                case NativeMethods.IDM_HIDEFULLSCREEN:
                    ToggleHideInFullscreen();
                    break;

                case NativeMethods.IDM_RESTART:
                    RestartWinMeters();
                    break;

                default:
                    WinMeters.Log.D($"DispatchMenuCommand: unknown cmd {cmd}");
                    break;
            }
        }

        /// <summary>Launches taskmgr.exe via the shell. Matches the kil0bit cmd-1002 handler.</summary>
        private static void LaunchTaskManager()
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
        /// Toggles <c>_settings.General.KeepOnTop</c> and re-applies
        /// the WPF Topmost flag + ZOrder timer gate via
        /// <see cref="ApplyKeepOnTop"/>. Mirrors kil0bit's
        /// <c>cmd == 1008</c> branch (<c>AlwaysOnTop = !AlwaysOnTop</c>).
        /// </summary>
        private void ToggleKeepOnTop()
        {
            _settings.General.KeepOnTop = !_settings.General.KeepOnTop;
            ApplyKeepOnTop(_settings.General.KeepOnTop);
            _settings.Save();
        }

        /// <summary>
        /// Toggles <c>_settings.General.HideInFullscreen</c>. The
        /// AppBar service's ABN_FULLSCREENAPP handler reads this flag
        /// on every fullscreen transition, so the change takes effect
        /// the next time a fullscreen app activates. Mirrors kil0bit's
        /// <c>cmd == 1009</c> branch.
        /// </summary>
        private void ToggleHideInFullscreen()
        {
            _settings.General.HideInFullscreen = !_settings.General.HideInFullscreen;
            _settings.Save();
        }

        /// <summary>
        /// Toggles <c>_settings.Window.LockPosition</c>. Persists the
        /// change (and the current X/Y) via <see cref="SavePosition"/>.
        /// Mirrors kil0bit's <c>cmd == 1006</c> branch.
        /// </summary>
        private void ToggleLockPosition()
        {
            _settings.Window.LockPosition = !_settings.Window.LockPosition;
            SavePosition();
        }

        /// <summary>
        /// Toggles <c>_settings.Window.StickToTaskbar</c> and routes
        /// the change through <see cref="ApplyWindowMode"/> so the
        /// AppBar service re-registers (or unregisters) and the bar
        /// re-anchors to the taskbar (or returns to floating mode).
        /// Mirrors kil0bit's <c>cmd == 1007</c> branch.
        /// </summary>
        private void ToggleSnapToTaskbar()
        {
            _settings.Window.StickToTaskbar = !_settings.Window.StickToTaskbar;
            ApplyWindowMode();
            _settings.Save();
        }

        /// <summary>
        /// Restarts WinMeters (cmd 1010, WinMeters extension beyond the
        /// kil0bit 1001-1009 ID space) so the user can pick up settings
        /// changes without manually closing + reopening the bar. The
        /// flow:
        ///
        ///   1. <see cref="SavePosition"/> persists the current X/Y so
        ///      the new instance launches at the same screen spot.
        ///   2. <see cref="App.ReleaseSingleInstanceMutex"/> drops the
        ///      kernel handle so the freshly-launched process can
        ///      acquire it without hitting the "already running" branch
        ///      in App.OnStartup. Without this, the new process
        ///      sometimes races the old one's OnExit mutex release and
        ///      shows a spurious "WinMeters is already running" dialog.
        ///   3. Process.Start launches a fresh WinMeters.exe with the
        ///      same executable path as the current process. The path
        ///      resolution handles both the standalone .exe (published
        ///      output) and the .dll + dotnet.exe pair (dotnet run /
        ///      dotnet test dev workflow).
        ///   4. Application.Current.Shutdown() tears the old process
        ///      down. MainWindow_Closed + App.OnExit fire normally and
        ///      dispose of services / tray / appbar / hotkey. The
        ///      <c>_singleInstanceMutex</c> field is already null (set
        ///      by step 2) so OnExit's release is a no-op.
        /// </summary>
        private void RestartWinMeters()
        {
            SavePosition();

            // Step 1: find the entry-point path. Assembly.GetEntryAssembly
            // is the only fully-trustable source for "what executable
            // (or dll) started me" - Environment.ProcessPath returns
            // dotnet.exe in the dotnet-run scenario, which is NOT what
            // we want to relaunch. If the entry assembly is a .dll we
            // launch via "dotnet path/to.dll"; otherwise the entry path
            // is itself the .exe.
            //
            // The .Location access is wrapped in a #pragma disable IL3000:
            // the WinMeters csproj sets <PublishSingleFile>true</PublishSingleFile>,
            // so .Location returns "" at runtime in published builds. The
            // empty-string fall-through below routes us to Environment.ProcessPath
            // (which returns the .exe path in single-file mode) and then
            // to Process.MainModule.FileName as a last resort. The fallback
            // chain covers both single-file and normal publishes; the
            // .Location call exists only to catch the dotnet-run dev
            // workflow where Environment.ProcessPath would return dotnet.exe.
            string? entryPath = null;
            try
            {
#pragma warning disable IL3000
                entryPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
            }
            catch { /* GetEntryAssembly can throw in some hosted scenarios; fall through */ }

            if (string.IsNullOrEmpty(entryPath))
            {
                entryPath = Environment.ProcessPath;
            }
            if (string.IsNullOrEmpty(entryPath))
            {
                entryPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            }
            if (string.IsNullOrEmpty(entryPath))
            {
                WinMeters.Log.D("RestartWinMeters: could not determine entry path; aborting restart.");
                return;
            }

            // Step 2: drop the single-instance mutex so the new process
            // can take ownership immediately. Done before Process.Start
            // so the new process doesn't race the old one's OnExit.
            try { App.ReleaseSingleInstanceMutex(); }
            catch (Exception ex) { WinMeters.Log.D($"RestartWinMeters: ReleaseSingleInstanceMutex: {ex.Message}"); }

            // Step 3: launch a fresh process. UseShellExecute=true so
            // the OS resolves any PATHEXT / shell-association quirks
            // (e.g. when the entry path is a .dll we still go through
            // "dotnet" + .dll, which is a registered file association).
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    UseShellExecute = true,
                };
                if (entryPath.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
                {
                    // dotnet-run / dotnet-test dev workflow: re-launch
                    // via dotnet so the host runtime is set up again.
                    psi.FileName = "dotnet";
                    psi.Arguments = $"\"{entryPath}\"";
                }
                else
                {
                    // Standalone published exe: relaunch the .exe directly.
                    psi.FileName = entryPath;
                }
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"RestartWinMeters: Process.Start failed: {ex.Message}");
                // We already released the mutex; the only safe way to
                // recover is to let the current process keep running
                // and let the user retry. OnExit will re-acquire / re-
                // release cleanly on the next normal exit.
                return;
            }

            // Step 4: tear down the old process. SavePosition already ran
            // (step 0); OnExit is no-op for mutex thanks to step 2.
            System.Windows.Application.Current.Shutdown();
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

        /// <summary>
        /// Opens the Settings dialog as a modeless owned window and
        /// (optionally) jumps to a specific section. Used by both the
        /// RMB-menu Settings entry (no section) and the RMB-menu About
        /// entry ("About" section), matching .Kilobit/OverlayWindow.cs
        /// where cmd 1003 (About) opens Settings and auto-selects the
        /// About section.
        ///
        /// Single-instance gate: if Settings is already open, just
        /// reactivate it (and re-navigate if a section was requested).
        /// Without this gate, switching from modal ShowDialog to modeless
        /// Show lets an impatient user spawn N independent SettingsWindow
        /// instances, each holding a private clone of _settings (the JSON
        /// round-trip in their ctor) and competing on close for which
        /// one's BtnSave_Click wins.
        ///
        /// Apply-on-save: the Closed subscriber fires after the dialog
        /// closes. If dlg.WasSaved (BtnSave_Click path), we run the full
        /// ApplySettings branch. If not (X-button or Esc), SettingsWindow's
        /// own SettingsWindow_Closing handler already restored the snapshot
        /// to _original before Closed even fires, so we leave _settings
        /// alone. We deliberately use the SettingsWindow.WasSaved bool
        /// rather than the WPF Window.DialogResult property -- Settings is
        /// shown modeless via Show() (so the user can drag the bar while
        /// it's up), and the WPF DialogResult setter throws when called on
        /// a Show()'d window.
        /// </summary>
        private void OpenSettingsAndNavigateTo(string? sectionName)
        {
            if (_existingSettingsWindow is { } existing)
            {
                existing.Activate();
                if (!string.IsNullOrEmpty(sectionName))
                {
                    existing.SelectSection(sectionName);
                }
                return;
            }

            var dlg = new SettingsWindow(_settings) { Owner = this };
            _existingSettingsWindow = dlg;

            dlg.Closed += (_, _) =>
            {
                try
                {
                    if (dlg.WasSaved)
                    {
                        ApplySettings();
                    }
                }
                finally
                {
                    // Always clear the cached reference so the next click
                    // opens a fresh Settings. finally runs regardless of
                    // whether ApplySettings throws, so a corrupt .json
                    // doesn't permanently lock the user out of Settings.
                    _existingSettingsWindow = null;
                }
            };

            if (!string.IsNullOrEmpty(sectionName))
            {
                dlg.SelectSection(sectionName);
            }
            dlg.Show();
        }

        /// <summary>
        /// Opens Settings (cmd 1001 from the popup menu; also called
        /// from the tray icon's left-double-click handler). Kept as a
        /// thin wrapper around <see cref="OpenSettingsAndNavigateTo"/>
        /// so the tray icon's old RoutedEventArgs-style invocation
        /// (<c>MenuItem_Settings_Click(this, new RoutedEventArgs())</c>)
        /// keeps working.
        /// </summary>
        private void MenuItem_Settings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsAndNavigateTo(null);
        }


        private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
        {
            SavePosition();
            try
            {
                _timer?.Stop();
                _monitorManager?.Dispose();
                _hardwareMonitor?.Dispose();
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"MenuItem_Exit_Click: {ex}");
            }
            System.Windows.Application.Current.Shutdown();
        }

        #endregion

        #region Settings Application

        /// <summary>
        /// Loads settings from disk and applies them.
        /// </summary>
        public void ApplySettings()
        {
            _settings = AppSettings.Load();
            // Refresh every service that captures a _settings reference at construction
            // so the AppBarService / WindowPlacementService read from the new instance.
            _appBarService?.BindSettings(_settings);
            _placementService.BindSettings(_settings);
            // Re-init / shut down the LibreHardwareMonitorService to match the new
            // EnableHardwareMonitor setting -- needed when the user toggled the
            // checkbox in SettingsWindow since the previous launch.
            ApplyHardwareMonitor();
            ClearCaches();
            ApplySettingsInternal();
            // Re-apply positioning in case the user changed WindowMode / MonitorIndex
            // since the previous load.
            ApplyWindowMode();
        }

        /// <summary>
        /// Applies settings for live preview (does not save to disk).
        /// </summary>
        public void ApplySettingsLive(AppSettings settings)
        {
            _settings = settings;
            _appBarService?.BindSettings(_settings);
            _placementService.BindSettings(_settings);
            // Also bring the LibreHardwareMonitorService up/down on the live path so
            // the sensor data updates immediately while the user is editing the
            // EnableHardwareMonitor checkbox in the open dialog. ApplySettings (the
            // post-save path called after BtnOk_Click) calls the same method so
            // the code path is identical regardless of caller.
            ApplyHardwareMonitor();
            ClearCaches();
            ApplySettingsInternal();
            // ApplyWindowMode is called by SettingsWindow.ApplyChangesLive directly
            // for quicker feedback on mode/monitor dropdowns; calling it again here
            // is also safe and makes ApplySettingsLive self-sufficient.
            ApplyWindowMode();
        }

        private void ClearCaches()
        {
            _lastRamPieSource = null;
            _lastRamPercentage = -1;
            _lastRamPieDpiBucket = -1;
            _lastGpuDedicatedSource = null;
            _lastGpuDedicatedPercentage = -1;
            _lastGpuDedicatedPieDpiBucket = -1;
            _lastGpuSharedSource = null;
            _lastGpuSharedPercentage = -1;
            _lastGpuSharedPieDpiBucket = -1;
            _lastNetDownFormatted = "";
            _lastNetUpFormatted = "";
            _lastDiskReadFormatted = "";
            _lastDiskWriteFormatted = "";
            _lastTimeFormatted = "";
            _lastTimeTicks = 0;
        }

        private void ApplySettingsInternal()
        {
            ConfigureTimer();
            ApplyDiskSettings();
            ApplyNetworkSettings();
            ApplyColors();
            ApplyScale();
            ApplyVisibility();
            ApplyMeterOrder();
            UpdateTooltips();
        }

        private void ApplyNetworkSettings()
        {
            // Apply the network interface filter to the monitor manager
            _monitorManager.InterfaceNameFilter = _settings.General.NetworkInterfaceName;
        }

        private void ConfigureTimer()
        {
            int minRate = CalculateMinRefreshRate();

            if (_timer == null)
            {
                _timer = new DispatcherTimer();
                _timer.Tick += Timer_Tick;
            }
            else
            {
                _timer.Stop();
            }

            _timer.Interval = TimeSpan.FromMilliseconds(minRate);
            _timer.Start();
        }

        private int CalculateMinRefreshRate()
        {
            int minRate = _settings.General.RefreshRateMs;
            if (_settings.Rates.Cpu.HasValue) minRate = Math.Min(minRate, _settings.Rates.Cpu.Value);
            if (_settings.Rates.Ram.HasValue) minRate = Math.Min(minRate, _settings.Rates.Ram.Value);
            if (_settings.Rates.Disk.HasValue) minRate = Math.Min(minRate, _settings.Rates.Disk.Value);
            if (_settings.Rates.Net.HasValue) minRate = Math.Min(minRate, _settings.Rates.Net.Value);
            if (_settings.Rates.GpuDedicated.HasValue) minRate = Math.Min(minRate, _settings.Rates.GpuDedicated.Value);
            if (_settings.Rates.GpuShared.HasValue) minRate = Math.Min(minRate, _settings.Rates.GpuShared.Value);
            if (_settings.Rates.GpuTemp.HasValue) minRate = Math.Min(minRate, _settings.Rates.GpuTemp.Value);
            if (_settings.Rates.CpuTemp.HasValue) minRate = Math.Min(minRate, _settings.Rates.CpuTemp.Value);

            return Math.Max(minRate, Constants.Timing.MinTimerIntervalMs);
        }

        private void ApplyDiskSettings()
        {
            string diskInstance = _settings.General.DiskInstanceName;
            _monitorManager.SetDiskInstance(diskInstance);
        }

        private void ApplyColors()
        {
            try
            {
                // Background & Border
                var bgBrush = ColorHelper.ParseBrush(_settings.Colors.Background);
                // In stick-to-taskbar mode, do NOT multiply brush.Opacity onto the
                // layered window's background. Multiplying alpha through the WPF
                // brush state forces DWM to recompose the layered surface on every
                // WPF invalidate cycle (CPU/RAM/Net/Disk text changes etc.) and
                // along with it the taskbar surface behind any anti-aliased or
                // rounded-corner regions. The user-visible symptom is the system
                // taskbar fading / changing opacity every refresh tick. WinMeters's
                // `UpdateLayeredWindow` path writes a pre-blended 32-bit ARGB
                // bitmap in one pass; the per-pixel alpha in
                // `_settings.Colors.Background` (e.g. the `CC` byte of `#CC202020`)
                // already encodes the desired translucency, so omit the multiplier
                // and rely on per-pixel alpha. The Opacity slider in the
                // SettingsWindow intentionally has no effect in stick mode (it
                // carries a tooltip explaining this and pointing users at the
                // Background color's alpha byte instead). Power users can still
                // program per-pixel alpha directly via the color picker.
                if (!_settings.Window.StickToTaskbar)
                {
                    bgBrush.Opacity = _settings.General.Opacity;
                }
                MainBorder.Background = bgBrush;
                MainBorder.BorderBrush = ColorHelper.ParseBrush(_settings.Colors.Border);
                MainBorder.BorderThickness = new Thickness(_settings.Colors.BorderThickness);

                // CPU
                SetupCpuBars();

                // RAM / VRAM / SRAM pies: rendered with GDI+ into a WPF WriteableBitmap
                // backbuffer (see Utils/PieChartRenderer.cs + RENDERING.md). The wedge
                // fill AND the border stroke are painted into the same bitmap, so neither
                // .Fill nor .Stroke brushes need to be assigned on the WPF Image host
                // here — UpdateRamMeter / UpdateGpuMemoryMeters pass the current colors
                // as parameters every tick.

                // Disk Labels — the "R:" / "W:" prefixes use the read/write colors.
                // The percentage values use the same colors so the meter stays self-consistent.
                var diskReadBrush = ColorHelper.ParseBrush(_settings.Colors.DiskRead);
                var diskWriteBrush = ColorHelper.ParseBrush(_settings.Colors.DiskWrite);
                DiskRestText.Foreground = diskReadBrush;
                DiskWriteLabel.Foreground = diskWriteBrush;
                DiskReadText.Foreground = diskReadBrush;
                DiskWriteText.Foreground = diskWriteBrush;

                // Network
                var netDownBrush = ColorHelper.ParseBrush(_settings.Colors.NetDown);
                var netUpBrush = ColorHelper.ParseBrush(_settings.Colors.NetUp);
                NetDownText.Foreground = netDownBrush;
                NetUpText.Foreground = netUpBrush;
                ArrowDown.Fill = netDownBrush;
                ArrowUp.Fill = netUpBrush;

                // Temperature displays
                var cpuBrush = ColorHelper.ParseBrush(_settings.Colors.CpuTemp);
                var gpuBrush = ColorHelper.ParseBrush(_settings.Colors.GpuTemp);
                CpuTempText.Foreground = cpuBrush;
                CpuTempLabel.Foreground = cpuBrush;
                CpuLoadText.Foreground = cpuBrush;
                GpuTempText.Foreground = gpuBrush;
                GpuTempLabel.Foreground = gpuBrush;
                GpuLoadText.Foreground = gpuBrush;

                // Time
                TimeText.Foreground = ColorHelper.ParseBrush(_settings.Colors.TimeText);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"ApplyColors: {ex}");
            }
        }

        private void ApplyScale()
        {
            // Lock the WPF window's DIP-height to AppBarService.BarHeightNormalDips × ScaleFactor
            // (= 40 × Scale). Pair with the Window xaml's SizeToContent="Width" so WPF stops
            // competing with the XAML for height — the WPF window's actual height always
            // equals the centring formula's winHPx ÷ DPI. The WM_WINDOWPOSCHANGING Y-centre
            // then lands at the *visual* centre of the WPF window. Before this fix the bar
            // drifted ~4-12 DIPs downward because the centring formula anchored to a constant
            // 32-DIP value while WinMeters' actual rendered height (CpuContainer 24 + Margin
            // 5+5 + 2-row panels each ~14×2 + Margin 5+5 ≈ 40 DIPs) didn't match. Set BEFORE
            // the early-return so first-load applies even when the saved Scale equals
            // MainScale.ScaleX's default.
            this.Height = 40 * _settings.General.Scale;

            if (Math.Abs(MainScale.ScaleX - _settings.General.Scale) <= 0.001) return;

            if (this.IsLoaded)
            {
                double oldBottom = this.Top + this.ActualHeight;
                MainScale.ScaleX = _settings.General.Scale;
                MainScale.ScaleY = _settings.General.Scale;
                this.UpdateLayout();
                this.Top = oldBottom - this.ActualHeight;
            }
            else
            {
                MainScale.ScaleX = _settings.General.Scale;
                MainScale.ScaleY = _settings.General.Scale;
            }
        }

        private void ApplyVisibility()
        {
            PanelCpu.Visibility = _settings.Visibility.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
            PanelRam.Visibility = _settings.Visibility.ShowRam ? Visibility.Visible : Visibility.Collapsed;
            PanelDisk.Visibility = _settings.Visibility.ShowDisk ? Visibility.Visible : Visibility.Collapsed;
            PanelNet.Visibility = _settings.Visibility.ShowNet ? Visibility.Visible : Visibility.Collapsed;
            PanelGpuDedicated.Visibility = _settings.Visibility.ShowGpuDedicated ? Visibility.Visible : Visibility.Collapsed;
            PanelGpuShared.Visibility = _settings.Visibility.ShowGpuShared ? Visibility.Visible : Visibility.Collapsed;
            PanelTime.Visibility = _settings.Visibility.ShowTime ? Visibility.Visible : Visibility.Collapsed;

            // Hardware panel visibility
            bool hwAvailable = _hardwareMonitor?.IsAvailable == true;
            bool showCpuTemp = _settings.Visibility.ShowCpuTemp && hwAvailable;
            bool showGpuTemp = _settings.Visibility.ShowGpuTemp && hwAvailable;
            bool showLoad = _settings.Visibility.ShowHardwareLoad && hwAvailable;

            PanelHardware.Visibility = (showCpuTemp || showGpuTemp) ? Visibility.Visible : Visibility.Collapsed;
            RowCpuTemp.Visibility = showCpuTemp ? Visibility.Visible : Visibility.Collapsed;
            RowGpuTemp.Visibility = showGpuTemp ? Visibility.Visible : Visibility.Collapsed;

            CpuLoadText.Visibility = showLoad ? Visibility.Visible : Visibility.Collapsed;
            GpuLoadText.Visibility = showLoad ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyMeterOrder()
        {
            if (PanelCpu.Parent is not StackPanel mainStack) return;

            var map = new Dictionary<string, UIElement>
            {
                ["Cpu"] = PanelCpu,
                ["Ram"] = PanelRam,
                ["Disk"] = PanelDisk,
                ["Net"] = PanelNet,
                ["GpuDedicated"] = PanelGpuDedicated,
                ["GpuShared"] = PanelGpuShared,
                ["Time"] = PanelTime
            };

            mainStack.Children.Clear();
            var addedPanels = new HashSet<UIElement>();

            foreach (var key in _settings.General.MeterOrder)
            {
                UIElement? panel = null;
                if (key is "CpuTemp" or "GpuTemp")
                {
                    panel = PanelHardware;
                }
                else if (map.TryGetValue(key, out var p))
                {
                    panel = p;
                }
                else
                {
                    WinMeters.Log.D($"ApplyMeterOrder: Unrecognized meter key '{key}' ignored.");
                    continue;
                }

                if (panel is null || addedPanels.Contains(panel)) continue;

                mainStack.Children.Add(panel);
                mainStack.Children.Add(CreateSeparator());
                addedPanels.Add(panel);
            }

            foreach (var kvp in map)
            {
                if (!addedPanels.Contains(kvp.Value))
                {
                    mainStack.Children.Add(kvp.Value);
                    mainStack.Children.Add(CreateSeparator());
                    addedPanels.Add(kvp.Value);
                }
            }

            // Remove trailing separator
            if (mainStack.Children.Count > 0 &&
                mainStack.Children[^1] is WpfRectangle { Width: 1 })
            {
                mainStack.Children.RemoveAt(mainStack.Children.Count - 1);
            }
        }

        private UIElement CreateSeparator()
        {
            return new WpfRectangle
            {
                Width = 1,
                Fill = ColorHelper.ParseBrush(_settings.Colors.Separator),
                Margin = new Thickness(0, 5, 0, 5)
            };
        }

        #endregion

        #region Timer & Updates

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // Don't do any UI-bound work before the window has been Loaded: the XAML bindings
            // to named children (RamPie, PanelCpu, etc.) are not guaranteed valid yet.
            if (!this.IsLoaded) return;

            long now = DateTime.UtcNow.Ticks;

            UpdateCpuMeters(now);
            UpdateRamMeter(now);
            UpdateDiskMeter(now);
            UpdateNetMeter(now);
            UpdateHardwareSensors(now);
            UpdateGpuMemoryMeters(now);
            UpdateTime(now);
            UpdateTooltips();

            // No continuous per-tick positioning: kil0bit keeps X/Y verbatim in
            // floating mode (the user drags the bar), and the shell + ABN_*
            // callbacks handle positioning in AppBar mode.
        }

        private void UpdateCpuMeters(long now)
        {
            if (!IsReadyToUpdate(ref _lastCpuTicks, _settings.Rates.Cpu ?? _settings.General.RefreshRateMs, now)) return;

            _monitorManager.UpdateCpu();
            var splitUsages = _monitorManager.GetCoreSplitUsages();
            int logicalCores = splitUsages.Length;
            int bars = _cpuBarSets.Count;
            double availableHeight = Math.Max(0, Constants.Display.CpuBarHeight - (2 * _settings.Colors.CpuBorderThickness));

            for (int i = 0; i < bars; i++)
            {
                var (total, user) = CalculateCoreUsage(splitUsages, i, logicalCores);
                double sys = Math.Max(0, total - user);
                double hSys = (sys / 100.0) * availableHeight;
                double hUser = (user / 100.0) * availableHeight;

                _cpuBarSets[i].RectSystem!.Height = hSys;
                _cpuBarSets[i].RectUser!.Height = hUser;
                _cpuBarSets[i].RectUser!.Margin = new Thickness(0, 0, 0, hSys);
            }
        }

        private (double Total, double User) CalculateCoreUsage((double Total, double User)[] splitUsages, int barIndex, int logicalCores)
        {
            double total, user;

            if (logicalCores > 1 && _settings.General.CombineLogicalCores)
            {
                int idx1 = barIndex * 2;
                int idx2 = idx1 + 1;

                double t1 = idx1 < logicalCores ? splitUsages[idx1].Total : 0;
                double u1 = idx1 < logicalCores ? splitUsages[idx1].User : 0;
                double t2 = idx2 < logicalCores ? splitUsages[idx2].Total : 0;
                double u2 = idx2 < logicalCores ? splitUsages[idx2].User : 0;

                total = (t1 + t2) / 2.0;
                user = (u1 + u2) / 2.0;
            }
            else
            {
                total = barIndex < logicalCores ? splitUsages[barIndex].Total : 0;
                user = barIndex < logicalCores ? splitUsages[barIndex].User : 0;
            }

            return (Math.Min(total, 100), Math.Min(user, 100));
        }

        private void UpdateRamMeter(long now)
        {
            if (!IsReadyToUpdate(ref _lastRamTicks, _settings.Rates.Ram ?? _settings.General.RefreshRateMs, now)) return;
            _monitorManager.UpdateRam();
            // Render the RAM pie via GDI+ into a WPF WriteableBitmap backbuffer. The
            // wedge fill and the border stroke are painted into the same bitmap; the
            // WPF Image element hosts the result. See Utils/PieChartRenderer.cs.
            PieChartRenderer.UpdatePieWithCache(
                RamPie,
                _monitorManager.RamUsage,
                _settings.Colors.RamBorderThickness,
                ColorHelper.ToDrawingColor(_settings.Colors.RamPie),
                ColorHelper.ToDrawingColor(_settings.Colors.RamBorder),
                _appBarService?.DpiScale ?? 1.0f,
                ref _lastRamPieSource,
                ref _lastRamPercentage,
                ref _lastRamPieDpiBucket);
        }

        private void UpdateDiskMeter(long now)
        {
            if (!IsReadyToUpdate(ref _lastDiskTicks, _settings.Rates.Disk ?? _settings.General.RefreshRateMs, now)) return;
            _monitorManager.UpdateDisk();

            string readFormatted = $"{_monitorManager.DiskReadUsage:F0}%";
            if (readFormatted != _lastDiskReadFormatted)
            {
                _lastDiskReadFormatted = readFormatted;
                DiskReadText.Text = readFormatted;
            }

            string writeFormatted = $"{_monitorManager.DiskWriteUsage:F0}%";
            if (writeFormatted != _lastDiskWriteFormatted)
            {
                _lastDiskWriteFormatted = writeFormatted;
                DiskWriteText.Text = writeFormatted;
            }
        }

        private void UpdateNetMeter(long now)
        {
            if (!IsReadyToUpdate(ref _lastNetTicks, _settings.Rates.Net ?? _settings.General.RefreshRateMs, now)) return;
            _monitorManager.UpdateNet();

            string downFormatted = FormatBytes(_monitorManager.NetDownload);
            if (downFormatted != _lastNetDownFormatted)
            {
                _lastNetDownFormatted = downFormatted;
                NetDownText.Text = downFormatted;
            }

            string upFormatted = FormatBytes(_monitorManager.NetUpload);
            if (upFormatted != _lastNetUpFormatted)
            {
                _lastNetUpFormatted = upFormatted;
                NetUpText.Text = upFormatted;
            }
        }

        private void UpdateHardwareSensors(long now)
        {
            if (_hardwareMonitor is not { IsAvailable: true }) return;

            int rateCpuTemp = _settings.Rates.CpuTemp ?? _settings.General.RefreshRateMs;
            int rateGpuTemp = _settings.Rates.GpuTemp ?? _settings.General.RefreshRateMs;
            bool needsCpuUpdate = IsReadyToUpdate(ref _lastCpuTempTicks, rateCpuTemp, now);
            bool needsGpuUpdate = IsReadyToUpdate(ref _lastGpuTempTicks, rateGpuTemp, now);

            if (!needsCpuUpdate && !needsGpuUpdate) return;

            try
            {
                _hardwareMonitor.Update();

                if (needsCpuUpdate)
                {
                    CpuTempText.Text = _hardwareMonitor.CpuTemperature.HasValue
                        ? $"{_hardwareMonitor.CpuTemperature:F0}°C" : "--°C";
                    CpuLoadText.Text = _hardwareMonitor.CpuLoad.HasValue
                        ? $"{_hardwareMonitor.CpuLoad:F0}%" : "--%";
                }

                if (needsGpuUpdate)
                {
                    GpuTempText.Text = _hardwareMonitor.GpuTemperature.HasValue
                        ? $"{_hardwareMonitor.GpuTemperature:F0}°C" : "--°C";
                    GpuLoadText.Text = _hardwareMonitor.GpuLoad.HasValue
                        ? $"{_hardwareMonitor.GpuLoad:F0}%" : "--%";
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"UpdateHardwareSensors: {ex}");
            }
        }

        private void UpdateGpuMemoryMeters(long now)
        {
            // Both GPU pies refresh together; let the slower rate gate the work.
            bool dedicatedDue = IsReadyToUpdate(ref _lastGpuDedicatedTicks, _settings.Rates.GpuDedicated ?? _settings.General.RefreshRateMs, now);
            bool sharedDue = IsReadyToUpdate(ref _lastGpuSharedTicks, _settings.Rates.GpuShared ?? _settings.General.RefreshRateMs, now);
            if (!dedicatedDue && !sharedDue) return;

            _monitorManager.UpdateGpu();

            // Ensure HardwareMonitorService is refreshed so GpuDedicatedMemoryUsage / GpuSharedMemoryUsage are current.
            _hardwareMonitor?.Update();

            if (dedicatedDue)
            {
                double percentage = ResolveGpuDedicatedPercentage();
                PieChartRenderer.UpdatePieWithCache(
                    GpuDedicatedPie,
                    percentage,
                    _settings.Colors.RamBorderThickness,
                    ColorHelper.ToDrawingColor(_settings.Colors.GpuDedicatedPie),
                    ColorHelper.ToDrawingColor(_settings.Colors.RamBorder),
                    _appBarService?.DpiScale ?? 1.0f,
                    ref _lastGpuDedicatedSource,
                    ref _lastGpuDedicatedPercentage,
                    ref _lastGpuDedicatedPieDpiBucket);
            }

            if (sharedDue)
            {
                double percentage = ResolveGpuSharedPercentage();
                PieChartRenderer.UpdatePieWithCache(
                    GpuSharedPie,
                    percentage,
                    _settings.Colors.RamBorderThickness,
                    ColorHelper.ToDrawingColor(_settings.Colors.GpuSharedPie),
                    ColorHelper.ToDrawingColor(_settings.Colors.RamBorder),
                    _appBarService?.DpiScale ?? 1.0f,
                    ref _lastGpuSharedSource,
                    ref _lastGpuSharedPercentage,
                    ref _lastGpuSharedPieDpiBucket);
            }
        }

        private double ResolveGpuDedicatedPercentage()
        {
            // Prefer an MB-derived ratio from HardwareMonitorService; it bypasses the 4 GB
            // overflow cap in Win32_VideoController.AdapterRAM. Fall back to the raw
            // percentage sensor and finally to MonitorManager's WMI-derived value.
            if (_hardwareMonitor?.GpuDedicatedMemoryUsed is { } used &&
                _hardwareMonitor?.GpuDedicatedMemoryTotal is { } total && total > 0)
            {
                return Math.Clamp((used / total) * 100.0, 0, 100);
            }
            if (_hardwareMonitor?.GpuDedicatedMemoryUsage is { } hwPct)
            {
                return Math.Clamp(hwPct, 0, 100);
            }
            return Math.Clamp(_monitorManager.GpuDedicatedUsage, 0, 100);
        }

        private double ResolveGpuSharedPercentage()
        {
            if (_hardwareMonitor?.GpuSharedMemoryUsed is { } used &&
                _hardwareMonitor?.GpuSharedMemoryTotal is { } total && total > 0)
            {
                return Math.Clamp((used / total) * 100.0, 0, 100);
            }
            if (_hardwareMonitor?.GpuSharedMemoryUsage is { } hwPct)
            {
                return Math.Clamp(hwPct, 0, 100);
            }
            return Math.Clamp(_monitorManager.GpuSharedUsage, 0, 100);
        }

        private void UpdateTime(long now)
        {
            if (!IsReadyToUpdate(ref _lastTimeTicks, Constants.Timing.ClockRefreshMs, now)) return;

            DateTime localTime = DateTime.Now;
            string format = _settings.General.Time24H ? "HH:mm" : "hh:mm tt";
            string timeStr = localTime.ToString(format);

            if (timeStr != _lastTimeFormatted)
            {
                _lastTimeFormatted = timeStr;
                TimeText.Text = timeStr;
            }

            PanelTime.ToolTip = localTime.ToLongDateString();
        }

        #endregion

        #region Tooltips

        private void UpdateTooltips()
        {
            try
            {
                // Build tooltip strings for all panels
                int coreCount = _monitorManager.LogicalCoreCount;
                string cpuMode = _settings.General.CombineLogicalCores ? "Combined" : "Individual";
                PanelCpu.ToolTip = $"CPU: {_monitorManager.CpuUsage:F1}%\n{coreCount} cores ({cpuMode})";

                double totalRamMb = _monitorManager.GetTotalRamMb();
                double usedRamMb = totalRamMb * (_monitorManager.RamUsage / 100.0);
                PanelRam.ToolTip = $"RAM: {_monitorManager.RamUsage:F1}%\n{usedRamMb / 1024:F1} / {totalRamMb / 1024:F1} GB";

                string diskName = _settings.General.DiskInstanceName ?? "_Total";
                PanelDisk.ToolTip = $"Disk: {diskName}\nRead: {_monitorManager.DiskReadUsage:F0}%\nWrite: {_monitorManager.DiskWriteUsage:F0}%";

                PanelNet.ToolTip = $"Network\n↓ {FormatBytes(_monitorManager.NetDownload)}\n↑ {FormatBytes(_monitorManager.NetUpload)}";

                string gpuName = _hardwareMonitor is { IsAvailable: true, GpuName: not null }
                    ? _hardwareMonitor.GpuName : "GPU";

                // Dedicated VRAM tooltip
                PanelGpuDedicated.ToolTip = FormatGpuMemoryTooltip(
                    gpuName,
                    "Dedicated VRam",
                    _monitorManager.GpuDedicatedUsage,
                    _hardwareMonitor?.GpuDedicatedMemoryUsed,
                    _hardwareMonitor?.GpuDedicatedMemoryTotal,
                    _monitorManager.GpuDedicatedTotal);

                // Shared SRAM tooltip
                PanelGpuShared.ToolTip = FormatGpuMemoryTooltip(
                    gpuName,
                    "Shared SRAM",
                    _monitorManager.GpuSharedUsage,
                    _hardwareMonitor?.GpuSharedMemoryUsed,
                    _hardwareMonitor?.GpuSharedMemoryTotal,
                    _monitorManager.GpuSharedTotal);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"UpdateTooltips: {ex}");
            }
        }

        private static string FormatGpuMemoryTooltip(
            string gpuName,
            string label,
            double usagePercentage,
            float? usedMb,
            float? totalMb,
            double totalBytes)
        {
            // Compute used bytes from MB (HardwareMonitorService provides MB values)
            double usedBytes = (usedMb ?? 0) * 1024.0 * 1024.0;
            // Compute total bytes from MB if available, otherwise fall back to totalBytes
            double total = 0;
            if ((totalMb ?? 0) > 0)
                total = (double)(totalMb ?? 0) * 1024.0 * 1024.0;
            else if (totalBytes > 0)
                total = totalBytes;

            // Determine the percentage to display: use the computed percentage from used/total,
            // but fall back to usagePercentage if we can't compute a meaningful ratio
            double perc = 0;
            if (total > 0 && usedBytes > 0)
                perc = (usedBytes / total) * 100.0;
            else if (usagePercentage > 0)
                perc = usagePercentage;

            // Format used/total in GB if we have valid byte values, otherwise show percentage
            if (total > 0 && usedBytes > 0)
                return $"{gpuName} {label}: {perc:F1}%\n{usedBytes / GiB:F2} / {total / GiB:F2} GB";
            return $"{gpuName} {label}: {perc:F1}%";
        }

        private const double GiB = 1024.0 * 1024.0 * 1024.0;

        #endregion

        #region Utilities

        private static bool IsReadyToUpdate(ref long lastUpdateTicks, int rateMs, long currentTicks)
        {
            long elapsedTicks = (currentTicks - lastUpdateTicks) / 10000;
            if (elapsedTicks >= rateMs)
            {
                lastUpdateTicks = currentTicks;
                return true;
            }
            return false;
        }

        private static string FormatBytes(double bytesPerSec)
        {
            const double kb = 1024.0;
            const double mb = kb * 1024;
            const double gb = kb * 1024 * 1024;

            return bytesPerSec < 1024 ? $"{bytesPerSec:F0} B/s" :
                   bytesPerSec < mb ? $"{bytesPerSec / kb:F1} KB/s" :
                   bytesPerSec < gb ? $"{bytesPerSec / mb:F1} MB/s" :
                   $"{bytesPerSec / gb:F1} GB/s";
        }

        #endregion
    }
}
