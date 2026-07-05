using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        #region Menu Event Handlers

        // WinMeters-order RMB menu, mirroring .Kilobit/OverlayWindow.cs WndProc
        // WM_RBUTTONUP handler:
        //   1. Utility actions: Settings (opens SettingsWindow, identical to legacy).
        //   2. View toggles: Keep on Top, Hide in Fullscreen, Lock Position,
        //      Snap to Taskbar — each gated on _settings and applied immediately
        //      so the toggle takes effect without a restart.
        //   3. About + Exit (separated by their own dividers in the XAML).

        private void MenuItem_Settings_Click(object sender, RoutedEventArgs e)
        {
            // Delegates to OpenSettingsAndNavigateTo(null) so the single-instance
            // gate, the cache + Closed subscriber, and the apply-on-save logic
            // live in exactly one place (also used by MenuItem_About_Click, which
            // additionally navigates to the "About" section).
            OpenSettingsAndNavigateTo(null);
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
        /// closes. If DialogResult == true (BtnSave_Click path), we run the
        /// full ApplySettings branch. If DialogResult != true (X-button or
        /// Esc), SettingsWindow's own SettingsWindow_Closing handler already
        /// restored the snapshot to _original before Closed even fires, so
        /// we leave _settings alone.
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
                    if (dlg.DialogResult == true)
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
        /// Position-aware placement of the RMB ContextMenu, matching
        /// .Kilobit/OverlayWindow.cs WM_RBUTTONUP: when the bar lives in
        /// the bottom half of the screen (typical for a taskbar-docked
        /// overlay), pop the menu UPWARD so it opens above the bar
        /// instead of overlapping it; when the bar lives in the top half,
        /// pop the menu DOWNWARD. WPF's ContextMenu.Placement=Top places
        /// the menu above the placement target; =Bottom places it below.
        /// VerticalOffset of +/- 4 leaves a 4-pixel gap between the menu
        /// and the bar edge, matching the kil0bit `my = wr.Top - 4` /
        /// `my = wr.Bottom + 4` constants in the popup-menu code.
        /// </summary>
        private void MainWindow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var cm = fe.ContextMenu;
            if (cm is null) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.RECT barRect;
            // GetWindowRect returns 0 on failure, nonzero on success (Win32
            // convention). Cannot be used as a bool — `!int` is a CS0023.
            if (NativeMethods.GetWindowRect(hwnd, out barRect) == 0) return;

            // Mirror the kil0bit check: barTop > midpoint of monitor working
            // area = bar is in the bottom half. System.Windows.Forms.Screen
            // is per-monitor DPI aware (returns the screen the HWND is on)
            // so its WorkingArea is in the same coordinate space as the
            // barRect from GetWindowRect -- direct comparison is safe.
            var screen = WnForms.Screen.FromHandle(hwnd);
            int midY = (screen.WorkingArea.Top + screen.WorkingArea.Bottom) / 2;

            if (barRect.Top > midY)
            {
                // Bar in bottom half -> pop menu UP
                cm.Placement = PlacementMode.Top;
                cm.VerticalOffset = -4;
            }
            else
            {
                // Bar in top half -> pop menu DOWN
                cm.Placement = PlacementMode.Bottom;
                cm.VerticalOffset = 4;
            }
        }

        private void MenuItem_TaskManager_Click(object sender, RoutedEventArgs e)
        {
            // WinMeters's "Task Manager" entry launches taskmgr.exe verbatim —
            // no flags, no arguments, no pre-check. Windows deduplicates at
            // the OS layer if one is already running.
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
                WinMeters.Log.D($"MenuItem_TaskManager_Click: {ex}");
            }
        }

        private void MenuItem_KeepOnTop_Click(object sender, RoutedEventArgs e)
        {
            // Mirrors kil0bit's _config.Config.AlwaysOnTop toggle. Applied
            // immediately (Topmost + ZOrderTimer gate) so the user sees the
            // change without a restart, and persisted via _settings.Save()
            // so the preference survives. ApplyKeepOnTop also runs from
            // OnSourceInitialized → ApplyWindowMode so the saved flag is
            // honoured at every boot.
            _settings.General.KeepOnTop = MenuKeepOnTop.IsChecked;
            ApplyKeepOnTop(_settings.General.KeepOnTop);
            _settings.Save();
        }

        private void MenuItem_HideInFullscreen_Click(object sender, RoutedEventArgs e)
        {
            // Mirrors kil0bit's _config.Config.HideOnFullscreen toggle. The
            // AppBar service's ABN_FULLSCREENAPP handler reads this flag
            // every time a fullscreen app activates / deactivates, so no
            // immediate visibility work is required here — the next
            // fullscreen transition picks up the new value automatically.
            _settings.General.HideInFullscreen = MenuHideInFullscreen.IsChecked;
            _settings.Save();
        }

        private void MenuItem_Lock_Click(object sender, RoutedEventArgs e)
        {
            _settings.Window.LockPosition = MenuLock.IsChecked;
            SavePosition();
        }

        private void MenuItem_SnapToTaskbar_Click(object sender, RoutedEventArgs e)
        {
            // WinMeters has a single "Snap to Taskbar" boolean. ApplyWindowMode
            // routes the change through the AppBar service (attach / detach
            // + saved X/Y restore) so we don't duplicate any logic here.
            _settings.Window.StickToTaskbar = MenuSnapToTaskbar.IsChecked;
            ApplyWindowMode();
            _settings.Save();
        }

        private void MenuItem_About_Click(object sender, RoutedEventArgs e)
        {
            // kil0bit parity: the About entry (cmd 1003 in
            // .Kilobit/OverlayWindow.cs WM_RBUTTONUP) opens Settings and
            // auto-navigates to the About section instead of popping a
            // transient MessageBox. The user gets a richer About view
            // (version pulled from the assembly, GitHub repo link) inside
            // the same UI surface as the rest of their settings, matching
            // the upstream kil0bit reference port. OpenSettingsAndNavigateTo
            // re-uses the single-instance gate and apply-on-save wiring so
            // the cached reference is shared with the Settings entry.
            OpenSettingsAndNavigateTo("About");
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
            ApplyMenuState();
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

        private void ApplyMenuState()
        {
            // Refresh the four checkable menu items from the live settings. The bar
            // is re-bound to settings via BindSettings() after SettingsWindow.Save(),
            // so this picks up both newly-loaded and freshly-edited values without
            // re-applying the full InitializeComponent cycle. Legacy menu names
            // (MenuDock / MenuStartup) were folded into the kil0bit-style 4-tuple:
            // MenuLock, MenuSnapToTaskbar, MenuKeepOnTop, MenuHideInFullscreen.
            MenuLock.IsChecked = _settings.Window.LockPosition;
            MenuSnapToTaskbar.IsChecked = _settings.Window.StickToTaskbar;
            MenuKeepOnTop.IsChecked = _settings.General.KeepOnTop;
            MenuHideInFullscreen.IsChecked = _settings.General.HideInFullscreen;
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
