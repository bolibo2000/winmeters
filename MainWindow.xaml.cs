using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Interop;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;
using WnForms = System.Windows.Forms;
using WinMeters.Utils;

namespace WinMeters
{
    public partial class MainWindow : Window, Services.IBarMenuDelegate
    {
        private readonly Monitors.MonitorManager _monitorManager;
        private Services.HotkeyService? _hotkeyService;
        private Services.WindowPlacementService _placementService = null!;
        private Monitors.HardwareMonitorService? _hardwareMonitor;
        private DispatcherTimer? _timer;
        private DispatcherTimer? _zOrderTimer;
        private AppSettings _settings = new();

        private long _lastCpuTicks, _lastRamTicks, _lastDiskTicks, _lastNetTicks;
        private long _lastCpuTempTicks, _lastGpuTempTicks, _lastGpuDedicatedTicks, _lastGpuSharedTicks;

        private string _lastNetDownFormatted = "", _lastNetUpFormatted = "";
        private string _lastDiskReadFormatted = "", _lastDiskWriteFormatted = "";

        private long _lastTimeTicks;
        private string _lastTimeFormatted = "", _lastDateFormatted = "";

        // Rate-limit tooltip rebuilds: only rebuild when meter data actually changes,
        // not on every timer tick. This avoids per-tick string allocations for tooltips
        // that rarely change (CPU%, RAM%, disk/net are already rate-limited by their
        // own IsReadyToUpdate gates; this adds a content-change gate on top).
        private string _lastTooltipCpu = "", _lastTooltipRam = "", _lastTooltipDisk = "", _lastTooltipNet = "";
        private string _lastTooltipGpuDedicated = "", _lastTooltipGpuShared = "";

        private readonly System.Text.StringBuilder _sbClassName = new(256);

        private WpfBitmapSource? _lastRamPieSource;
        private double _lastRamPercentage = -1;
        private int _lastRamPieDpiBucket = -1;
        private WpfBitmapSource? _lastGpuDedicatedSource;
        private double _lastGpuDedicatedPercentage = -1;
        private int _lastGpuDedicatedPieDpiBucket = -1;
        private WpfBitmapSource? _lastGpuSharedSource;
        private double _lastGpuSharedPercentage = -1;
        private int _lastGpuSharedPieDpiBucket = -1;

        private Services.AppBarService _appBarService = null!;
        private Services.BarPopupMenuService? _popupService;
        private WnForms.NotifyIcon? _trayIcon;
        private SettingsWindow? _existingSettingsWindow;
        private AboutWindow? _existingAboutWindow;

        public MainWindow()
        {
            InitializeComponent();

            // PerformanceCounterCategory .ctors can throw on systems with corrupt performance-
            // counter registrations (some Windows Server SKUs, after package uninstall). Let the
            // exception propagate but record it before re-throwing so the global
            // UnhandledException sink surfaces it to the rolling error log; the App's handler
            // will still pop the standard "fatal error" dialog to the user.
            Monitors.MonitorManager? monitorManager = null;
            try
            {
                monitorManager = new Monitors.MonitorManager();
            }
            catch (Exception ex)
            {
                WinMeters.Log.E(ex, "MainWindow: MonitorManager init failed.");
            }
            _monitorManager = monitorManager ?? new Monitors.MonitorManager();

            _settings = AppSettings.Load();
            _placementService = new Services.WindowPlacementService(this, _settings);
            InitializeHardwareMonitor();
            ApplySettingsInternal();
            _settings.Save();
            InitializeTrayIcon();

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
            this.Deactivated += MainWindow_Deactivated;
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _trayIcon = new WnForms.NotifyIcon
                {
                    Icon = LoadAppIcon(),
                    Text = "WinMeters",
                    Visible = true,
                };
                _trayIcon.ContextMenuStrip = BuildTrayMenu();
                _trayIcon.MouseDoubleClick += (_, args) =>
                {
                    if (args.Button == WnForms.MouseButtons.Left)
                        Dispatcher.Invoke(() => MenuItem_Settings_Click(this, new RoutedEventArgs()));
                };
            }
            catch (Exception ex) { WinMeters.Log.D($"InitializeTrayIcon failed: {ex.Message}"); }
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "winmeters.ico");
            try
            {
                if (System.IO.File.Exists(iconPath))
                    return new System.Drawing.Icon(iconPath);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"LoadAppIcon failed for '{iconPath}': {ex.Message}");
            }
            return System.Drawing.SystemIcons.Application;
        }

        // Tag attached to the "Show/Hide Bar" tray toggle item so ToggleVisibility can find and
        // update it in place without rebuilding the entire ContextMenuStrip and re-subscribing
        // every closure inside the parent menu — avoids a small allocation churn + transient
        // disposal window race each time the user toggles the bar from the tray.
        private const string ToggleVisibilityItemTag = "ToggleVisibility";

        private WnForms.ContextMenuStrip BuildTrayMenu()
        {
            bool isBarVisible = !_settings.Window.IsHiddenByUser;
            var menu = new WnForms.ContextMenuStrip();

            var settingsItem = new WnForms.ToolStripMenuItem("Show Settings");
            settingsItem.Click += (_, _) => Dispatcher.Invoke(() => MenuItem_Settings_Click(this, new RoutedEventArgs()));

            var toggleItem = new WnForms.ToolStripMenuItem(isBarVisible ? "Hide Bar" : "Show Bar")
            {
                Checked = isBarVisible,
                Tag = ToggleVisibilityItemTag
            };
            toggleItem.Click += (_, _) => Dispatcher.Invoke(() => ToggleVisibility());

            var aboutItem = new WnForms.ToolStripMenuItem("About");
            aboutItem.Click += (_, _) => Dispatcher.Invoke(() => OpenAboutWindow());

            var quitItem = new WnForms.ToolStripMenuItem("Quit");
            quitItem.Click += (_, _) => Dispatcher.Invoke(() => MenuItem_Exit_Click(this, new RoutedEventArgs()));

            menu.Items.Add(settingsItem);
            menu.Items.Add(toggleItem);
            menu.Items.Add(new WnForms.ToolStripSeparator());
            menu.Items.Add(aboutItem);
            menu.Items.Add(quitItem);
            return menu;
        }

        private void InitializeHardwareMonitor()
        {
            if (_hardwareMonitor is not null) return;
            if (!_settings.General.EnableHardwareMonitor) return;
            try
            {
                _hardwareMonitor = new Monitors.HardwareMonitorService(enableCpu: true, enableGpu: true, enableMotherboard: true);
            }
            catch (Exception ex) { WinMeters.Log.D($"Failed to initialize hardware monitor: {ex.Message}"); }
        }

        private void ApplyHardwareMonitor()
        {
            if (_settings.General.EnableHardwareMonitor)
                InitializeHardwareMonitor();
            else if (_hardwareMonitor is not null)
            {
                _hardwareMonitor.Dispose();
                _hardwareMonitor = null;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SetupCpuBars();
            UpdateTooltips();

            WinMeters.Log.D($"[WinMeters] Window loaded: Visibility={this.Visibility}, Opacity={this.Opacity}, Width={this.Width}, Height={this.ActualHeight}, Left={this.Left}, Top={this.Top}");
            WinMeters.Log.D($"[WinMeters] IsHiddenByUser={_settings.Window.IsHiddenByUser}, StickToTaskbar={_settings.Window.StickToTaskbar}");

            if (_settings.Window.IsHiddenByUser)
            {
                this.Visibility = Visibility.Collapsed;
                WinMeters.Log.D("[WinMeters] Window hidden due to IsHiddenByUser=true");
            }
            else
                this.Visibility = Visibility.Visible;

            // The visibility gate above may have toggled state that ApplyKeepOnTop needs to know
            // about before the timer starts — re-evaluate now so the z-order dispatcher timer only
            // spins up while we're actually on-screen and configured to keep-on-top.
            ApplyKeepOnTop(_settings.General.KeepOnTop);
        }

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
                ? ((logicalCores > 1) ? logicalCores / 2 : 1) : logicalCores;
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

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_settings.Window.LockPosition) this.DragMove();
        }

        private void SavePosition()
        {
            _placementService.SaveCurrentDips();
            _settings.Save();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
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
            catch (Exception ex) { WinMeters.Log.D($"MainWindow_Closed: {ex}"); }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            Services.ThemeService.InitializeDarkMode();

            var helper = new WindowInteropHelper(this);
            var source = HwndSource.FromHwnd(helper.Handle);
            if (source is null) return;

        _hotkeyService = new Services.HotkeyService(helper.Handle, ToggleVisibility, _settings.General.Hotkey);
        source.AddHook(_hotkeyService.HwndHook);
        // Subscribe BEFORE Register so a collision during the very first register attempt is
        // surfaced. The handler is resubscribed across ReRegister via the closure field, but
        // since RegisterFailed is a single multicast event, += on a freshly-constructed
        // HotkeyService instance is idempotent in practice (current HotkeyService is owned
        // per-call, not reused across the lifetime of MainWindow).
        _hotkeyService.RegisterFailed += OnHotkeyRegisterFailed;
        _hotkeyService.Register();

            _appBarService = new Services.AppBarService(this, _settings);
            source.AddHook(_appBarService.HwndHook);
            source.AddHook(MonitorChangeHook);

            _popupService = new Services.BarPopupMenuService(helper.Handle, _settings, this);
            source.AddHook(_popupService.WmRButtonUp);

            ApplyWindowMode();
        }

        public void ApplyWindowMode()
        {
            double savedXDip = _placementService.GetX();
            double savedYDip = _placementService.GetY();
            _appBarService.ApplyIntegrationState(savedXDip, savedYDip);
            ApplyKeepOnTop(_settings.General.KeepOnTop);
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

        private void ApplyKeepOnTop(bool keepOnTop)
        {
            this.Topmost = keepOnTop;
            // The z-order enforcement is only needed while visible AND keep-on-top. Pausing
            // the 500ms dispatcher timer when hidden avoids ~7200 wasted GetForegroundWindow
            // + GetClassName round-trips per hour when the user has chosen to hide the bar.
            if (keepOnTop && this.Visibility == Visibility.Visible)
            {
                StartZOrderTimer();
                return;
            }
            StopZOrderTimer();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        private void EnforceZOrder(object? sender, EventArgs e)
        {
            if (!_settings.General.KeepOnTop || _settings.Window.StickToTaskbar || this.Visibility != Visibility.Visible) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;

            _sbClassName.Clear();
            if (NativeMethods.GetClassName(fg, _sbClassName, _sbClassName.Capacity) <= 0) return;

            string fgClass = _sbClassName.ToString();
            if (fgClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;

            IntPtr prev = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV);
            if (prev == IntPtr.Zero) return;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private IntPtr MonitorChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_DISPLAYCHANGE)
            {
                WinMeters.Log.D("MainWindow: WM_DISPLAYCHANGE received.");
                ApplyWindowMode();
            }
            return IntPtr.Zero;
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e) { }

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
            }
            _settings.Save();

            // Re-evaluate whether the z-order keep-alive timer should be running: when hidden,
            // it shouldn't. When shown again, restart it if keep-on-top is on.
            ApplyKeepOnTop(_settings.General.KeepOnTop);

            // Update just the "Hide Bar"/"Show Bar" item in place if a tray menu already exists;
            // only build a fresh menu if the tray icon has no menu yet (e.g. first toggle).
            RefreshTrayMenuToggleItem();
        }

        private void RefreshTrayMenuToggleItem()
        {
            if (_trayIcon?.ContextMenuStrip is not { } menu) return;
            // Look for our flagged toggle item without disposing/rebuilding the whole menu —
            // anything else clicked through the tray between the toggle Click and the menu reopen
            // is still safely bound to live closures.
            foreach (WnForms.ToolStripItem item in menu.Items)
            {
                if (item is WnForms.ToolStripMenuItem tsi &&
                    (tsi.Tag as string) == ToggleVisibilityItemTag)
                {
                    bool isBarVisible = !_settings.Window.IsHiddenByUser;
                    tsi.Text = isBarVisible ? "Hide Bar" : "Show Bar";
                    tsi.Checked = isBarVisible;
                    break;
                }
            }
        }

        #region IBarMenuDelegate

        public void HandleShowSettings() => OpenSettings();
        public void HandleOpenTaskManager() => Services.BarPopupMenuService.LaunchTaskManager();
        public void HandleOpenAbout() => OpenAboutWindow();
        public void HandleExit() => MenuItem_Exit_Click(this, new RoutedEventArgs());
        public void HandleRestart() => RestartWinMeters();
        public void HandleToggleLock() => ToggleLockPosition();
        public void HandleToggleSnap() => ToggleSnapToTaskbar();
        public void HandleToggleKeepOnTop() => ToggleKeepOnTop();
        public void HandleToggleHideInFullscreen() => ToggleHideInFullscreen();

        private void ToggleKeepOnTop()
        {
            _settings.General.KeepOnTop = !_settings.General.KeepOnTop;
            ApplyKeepOnTop(_settings.General.KeepOnTop);
            _settings.Save();
        }

        private void ToggleHideInFullscreen()
        {
            _settings.General.HideInFullscreen = !_settings.General.HideInFullscreen;
            _settings.Save();
        }

        private void ToggleLockPosition()
        {
            _settings.Window.LockPosition = !_settings.Window.LockPosition;
            SavePosition();
        }

        private void ToggleSnapToTaskbar()
        {
            _settings.Window.StickToTaskbar = !_settings.Window.StickToTaskbar;
            ApplyWindowMode();
            _settings.Save();
        }

        private void RestartWinMeters()
        {
            SavePosition();

            string? entryPath = GetEntryAssemblyPath();
            if (string.IsNullOrEmpty(entryPath))
            {
                WinMeters.Log.E("RestartWinMeters: could not determine entry path; aborting.");
                ShowRestartFailed("WinMeters could not determine its own executable path, so the restart cannot proceed automatically.\n\nPlease relaunch WinMeters manually.");
                return;
            }

            try { App.ReleaseSingleInstanceMutex(); }
            catch (Exception ex) { WinMeters.Log.D($"RestartWinMeters: ReleaseSingleInstanceMutex: {ex.Message}"); }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = true };
                if (entryPath.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "dotnet";
                    psi.Arguments = $"\"{entryPath}\"";
                }
                else
                    psi.FileName = entryPath;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                WinMeters.Log.E(ex, "RestartWinMeters: Process.Start failed");
                ShowRestartFailed($"WinMeters failed to launch a new instance:\n\n{ex.Message}\n\nPlease relaunch WinMeters manually.");
                return;
            }

            System.Windows.Application.Current.Shutdown();
        }

        private static string? GetEntryAssemblyPath()
        {
            // Try each candidate in order; the first non-empty *and existing* path wins.
            // The prior shape returned from `Assembly.GetEntryAssembly()?.Location`
            // unconditionally on empty strings — but for single-file / ReadyToRun / AOT
            // scenarios Location is a perfectly valid "" (not null), which then
            // bypasses Environment.ProcessPath + MainModule.FileName and surfaces the
            // "could not determine executable path" message box even though we could
            // have answered the question from the process itself.
            foreach (var candidate in EnumerateEntryPathCandidates())
            {
                if (!string.IsNullOrEmpty(candidate) && System.IO.File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static IEnumerable<string> EnumerateEntryPathCandidates()
        {
            var fromEntry = SafeInvoke(TryGetEntryAssemblyLocationRaw, "entry assembly");
            if (!string.IsNullOrEmpty(fromEntry)) yield return fromEntry;

            var processPath = SafeInvoke(static () => Environment.ProcessPath, "Environment.ProcessPath");
            if (!string.IsNullOrEmpty(processPath)) yield return processPath;

            var mainModule = SafeInvoke(static () => System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName, "MainModule.FileName");
            if (!string.IsNullOrEmpty(mainModule)) yield return mainModule;
        }

#pragma warning disable IL3000
        private static string? TryGetEntryAssemblyLocationRaw()
            => System.Reflection.Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000

        private static string? SafeInvoke(Func<string?> getter, string label)
        {
            // Single funnel for every path-resolution source: each Getter is
            // tried in turn, exceptions are swallowed + logged (the path is
            // user-visible at restart time so a noisy log beat here), and a
            // null return is forwarded so the iterator yields nothing for
            // that slot. Owning the try/catch in one place keeps the iterator
            // block yield-safe (CS1626 forbids yield inside try-with-catch).
            try { return getter(); }
            catch (Exception ex) { WinMeters.Log.D($"GetEntryAssemblyPath: {label}: {ex.Message}"); return null; }
        }

        private static void ShowRestartFailed(string message)
        {
            try
            {
                System.Windows.MessageBox.Show(message, "WinMeters - Restart failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception mboxEx)
            {
                // If the WPF session is shutting down MessageBox can throw; in that case the only
                // thing we can usefully do is log to the error file.
                WinMeters.Log.E(mboxEx, "ShowRestartFailed: MessageBox.Show threw");
            }
        }

    private void OpenSettings()
    {
        if (_existingSettingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var dlg = new SettingsWindow(_settings) { Owner = this };
        _existingSettingsWindow = dlg;
        dlg.Closed += (_, _) =>
        {
            try { if (dlg.WasSaved) ApplySettings(); }
            finally { _existingSettingsWindow = null; }
        };
        dlg.Show();
    }

    private void OnHotkeyRegisterFailed(string message)
    {
        // Surface a one-shot tray balloon-tip so the user sees their saved Hotkey setting
        // is not actually registered (typically because another OS / app process owns the
        // chord). The tray icon is owned by InitializeTrayIcon which runs in the ctor — so
        // it's never null by the time this handler fires. We null-check defensively
        // because a Dispose path on the tray could run while a hotkey re-register is in
        // flight (e.g. during shutdown). ToolTipIcon.Warning picks the OS warning icon
        // for a clearer affordance than the Info default. BalloonTipText is the raw
        // message so the user can paste it into a bug report without reading a diff.
        // No local try/catch: any exception thrown by ShowBalloonTip on a stale tray
        // icon (e.g. disposed during a race with shutdown) is already swallowed by the
        // RegisterFailed raise site in HotkeyService.Register, which logs it to the
        // rolling debug log on the way out.
        _trayIcon?.ShowBalloonTip(
            timeout: 5000,
            tipTitle: "WinMeters — hotkey registration failed",
            tipText: message,
            tipIcon: ToolTipIcon.Warning);
    }

    private void MenuItem_Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

        private void OpenAboutWindow()
        {
            if (_existingAboutWindow is { } existing)
            {
                existing.Activate();
                return;
            }
            // Pass the live hotkey from the current settings so the About window reflects
            // whatever the user has saved (instead of the stale "Ctrl+Alt+Shift+M"
            // hardcoded into the original XAML). Closing + reopening About will pick up
            // updates applied via Settings between sessions.
            var dlg = new AboutWindow(_settings.General.Hotkey) { Owner = this };
            _existingAboutWindow = dlg;
            dlg.Closed += (_, _) => _existingAboutWindow = null;
            dlg.Show();
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
            catch (Exception ex) { WinMeters.Log.D($"MenuItem_Exit_Click: {ex}"); }
            System.Windows.Application.Current.Shutdown();
        }

        #endregion

        #region Settings Application

    public void ApplySettings()
    {
        _settings = AppSettings.Load();
        _appBarService?.BindSettings(_settings);
        _placementService.BindSettings(_settings);
        _popupService?.BindSettings(_settings);
        _hotkeyService?.BindSettings(_settings);
        _hotkeyService?.ReRegister();
        ApplyHardwareMonitor();
        ClearCaches();
        ApplySettingsInternal();
        ApplyWindowMode();
        SyncAboutHotkey();
    }

    public void ApplySettingsLive(AppSettings settings)
    {
        _settings = settings;
        _appBarService?.BindSettings(_settings);
        _placementService.BindSettings(_settings);
        _popupService?.BindSettings(_settings);
        _hotkeyService?.BindSettings(_settings);
        _hotkeyService?.ReRegister();
        ApplyHardwareMonitor();
        ClearCaches();
        ApplySettingsInternal();
        ApplyWindowMode();
        SyncAboutHotkey();
    }

    /// <summary>
    /// Pushes the current settings' hotkey into the About window if it's open.
    /// Without this, the About hint row was frozen at ctor time: opening About,
    /// then changing the chord in Settings, would leave the still-open About
    /// window showing the stale chord (the exact user-reported "hotkey does not
    /// update" symptom). <c>IsLoaded</c> guards against pushing text into a
    /// window that's been closed but not yet nulled out by the Closed handler.
    /// Note: <c>IsVisible</c> is intentionally NOT checked — the bug must
    /// still update when the window is minimized or behind another window,
    /// so the user sees the new chord on next restore/focus.
    /// </summary>
    private void SyncAboutHotkey()
    {
        try
        {
            if (_existingAboutWindow is { IsLoaded: true } win)
                win.SetHotkey(_settings.General.Hotkey);
        }
        catch (Exception ex) { WinMeters.Log.D($"SyncAboutHotkey: {ex.Message}"); }
    }

        private void ClearCaches()
        {
            _lastRamPieSource = null; _lastRamPercentage = -1; _lastRamPieDpiBucket = -1;
            _lastGpuDedicatedSource = null; _lastGpuDedicatedPercentage = -1; _lastGpuDedicatedPieDpiBucket = -1;
            _lastGpuSharedSource = null; _lastGpuSharedPercentage = -1; _lastGpuSharedPieDpiBucket = -1;
            _lastNetDownFormatted = ""; _lastNetUpFormatted = "";
            _lastDiskReadFormatted = ""; _lastDiskWriteFormatted = "";
            _lastTimeFormatted = ""; _lastDateFormatted = ""; _lastTimeTicks = 0;
            _lastTooltipCpu = ""; _lastTooltipRam = ""; _lastTooltipDisk = "";
            _lastTooltipNet = ""; _lastTooltipGpuDedicated = ""; _lastTooltipGpuShared = "";
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

        private void ApplyNetworkSettings() =>
            _monitorManager.InterfaceNameFilter = _settings.General.NetworkInterfaceName;

        private void ConfigureTimer()
        {
            int minRate = CalculateMinRefreshRate();
            if (_timer == null)
            {
                _timer = new DispatcherTimer();
                _timer.Tick += Timer_Tick;
            }
            else
                _timer.Stop();
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

        private void ApplyDiskSettings() => _monitorManager.SetDiskInstance(_settings.General.DiskInstanceName);

        private void ApplyColors()
        {
            try
            {
                var bgBrush = ColorHelper.ParseBrush(_settings.Colors.Background);
                if (_settings.Window.StickToTaskbar)
                {
                    var c = bgBrush.Color;
                    byte a = (byte)Math.Round(Math.Clamp(c.A * _settings.General.Opacity, 0, 255));
                    bgBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B));
                }
                else
                    bgBrush.Opacity = _settings.General.Opacity;
                MainBorder.Background = bgBrush;
                MainBorder.BorderBrush = ColorHelper.ParseBrush(_settings.Colors.Border);
                MainBorder.BorderThickness = new Thickness(_settings.Colors.BorderThickness);

                SetupCpuBars();

                var diskReadBrush = ColorHelper.ParseBrush(_settings.Colors.DiskRead);
                var diskWriteBrush = ColorHelper.ParseBrush(_settings.Colors.DiskWrite);
                DiskRestText.Foreground = diskReadBrush;
                DiskWriteLabel.Foreground = diskWriteBrush;
                DiskReadText.Foreground = diskReadBrush;
                DiskWriteText.Foreground = diskWriteBrush;

                var netDownBrush = ColorHelper.ParseBrush(_settings.Colors.NetDown);
                var netUpBrush = ColorHelper.ParseBrush(_settings.Colors.NetUp);
                NetDownText.Foreground = netDownBrush;
                NetUpText.Foreground = netUpBrush;
                ArrowDown.Fill = netDownBrush;
                ArrowUp.Fill = netUpBrush;

                var cpuBrush = ColorHelper.ParseBrush(_settings.Colors.CpuTemp);
                var gpuBrush = ColorHelper.ParseBrush(_settings.Colors.GpuTemp);
                CpuTempText.Foreground = cpuBrush;
                CpuTempLabel.Foreground = cpuBrush;
                CpuLoadText.Foreground = cpuBrush;
                GpuTempText.Foreground = gpuBrush;
                GpuTempLabel.Foreground = gpuBrush;
                GpuLoadText.Foreground = gpuBrush;

                TimeText.Foreground = ColorHelper.ParseBrush(_settings.Colors.TimeText);
            }
            catch (Exception ex) { WinMeters.Log.D($"ApplyColors: {ex}"); }
        }

        private void ApplyScale()
        {
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
                    panel = PanelHardware;
                else if (map.TryGetValue(key, out var p))
                    panel = p;
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

            if (mainStack.Children.Count > 0 &&
                mainStack.Children[^1] is WpfRectangle { Width: 1 })
                mainStack.Children.RemoveAt(mainStack.Children.Count - 1);
        }

        private UIElement CreateSeparator() =>
            new WpfRectangle
            {
                Width = 1,
                Fill = ColorHelper.ParseBrush(_settings.Colors.Separator),
                Margin = new Thickness(0, 5, 0, 5)
            };

        #endregion

        #region Timer & Updates

        private void Timer_Tick(object? sender, EventArgs e)
        {
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
            if (logicalCores > 1 && _settings.General.CombineLogicalCores)
            {
                int idx1 = barIndex * 2;
                int idx2 = idx1 + 1;
                double t1 = idx1 < logicalCores ? splitUsages[idx1].Total : 0;
                double u1 = idx1 < logicalCores ? splitUsages[idx1].User : 0;
                double t2 = idx2 < logicalCores ? splitUsages[idx2].Total : 0;
                double u2 = idx2 < logicalCores ? splitUsages[idx2].User : 0;
                return (Math.Min((t1 + t2) / 2.0, 100), Math.Min((u1 + u2) / 2.0, 100));
            }

            double total = barIndex < logicalCores ? splitUsages[barIndex].Total : 0;
            double user  = barIndex < logicalCores ? splitUsages[barIndex].User  : 0;
            return (Math.Min(total, 100), Math.Min(user, 100));
        }

        private void UpdateRamMeter(long now)
        {
            if (!IsReadyToUpdate(ref _lastRamTicks, _settings.Rates.Ram ?? _settings.General.RefreshRateMs, now)) return;
            _monitorManager.UpdateRam();
            PieChartRenderer.UpdatePieWithCache(
                RamPie, _monitorManager.RamUsage,
                _settings.Colors.RamBorderThickness,
                ColorHelper.ToDrawingColor(_settings.Colors.RamPie),
                ColorHelper.ToDrawingColor(_settings.Colors.RamBorder),
                _appBarService?.DpiScale ?? 1.0f,
                ref _lastRamPieSource, ref _lastRamPercentage, ref _lastRamPieDpiBucket);
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
            catch (Exception ex) { WinMeters.Log.D($"UpdateHardwareSensors: {ex}"); }
        }

        private void UpdateGpuMemoryMeters(long now)
        {
            bool dedicatedDue = IsReadyToUpdate(ref _lastGpuDedicatedTicks, _settings.Rates.GpuDedicated ?? _settings.General.RefreshRateMs, now);
            bool sharedDue = IsReadyToUpdate(ref _lastGpuSharedTicks, _settings.Rates.GpuShared ?? _settings.General.RefreshRateMs, now);
            if (!dedicatedDue && !sharedDue) return;

            _monitorManager.UpdateGpu();

            if (dedicatedDue)
            {
                double percentage = ResolveGpuDedicatedPercentage();
                PieChartRenderer.UpdatePieWithCache(
                    GpuDedicatedPie, percentage,
                    _settings.Colors.RamBorderThickness,
                    ColorHelper.ToDrawingColor(_settings.Colors.GpuDedicatedPie),
                    ColorHelper.ToDrawingColor(_settings.Colors.RamBorder),
                    _appBarService?.DpiScale ?? 1.0f,
                    ref _lastGpuDedicatedSource, ref _lastGpuDedicatedPercentage, ref _lastGpuDedicatedPieDpiBucket);
            }

            if (sharedDue)
            {
                double percentage = ResolveGpuSharedPercentage();
                PieChartRenderer.UpdatePieWithCache(
                    GpuSharedPie, percentage,
                    _settings.Colors.RamBorderThickness,
                    ColorHelper.ToDrawingColor(_settings.Colors.GpuSharedPie),
                    ColorHelper.ToDrawingColor(_settings.Colors.RamBorder),
                    _appBarService?.DpiScale ?? 1.0f,
                    ref _lastGpuSharedSource, ref _lastGpuSharedPercentage, ref _lastGpuSharedPieDpiBucket);
            }
        }

        private double ResolveGpuDedicatedPercentage()
        {
            if (_hardwareMonitor?.GpuDedicatedMemoryUsed is { } used &&
                _hardwareMonitor?.GpuDedicatedMemoryTotal is { } total && total > 0)
                return Math.Clamp((used / total) * 100.0, 0, 100);
            if (_hardwareMonitor?.GpuDedicatedMemoryUsage is { } hwPct)
                return Math.Clamp(hwPct, 0, 100);
            return Math.Clamp(_monitorManager.GpuDedicatedUsage, 0, 100);
        }

        private double ResolveGpuSharedPercentage()
        {
            if (_hardwareMonitor?.GpuSharedMemoryUsed is { } used &&
                _hardwareMonitor?.GpuSharedMemoryTotal is { } total && total > 0)
                return Math.Clamp((used / total) * 100.0, 0, 100);
            if (_hardwareMonitor?.GpuSharedMemoryUsage is { } hwPct)
                return Math.Clamp(hwPct, 0, 100);
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

            string dateStr = localTime.ToLongDateString();
            if (dateStr != _lastDateFormatted)
            {
                _lastDateFormatted = dateStr;
                PanelTime.ToolTip = dateStr;
            }
        }

        #endregion

        #region Tooltips

        private void UpdateTooltips()
        {
            try
            {
                string cpuTip = $"CPU: {_monitorManager.CpuUsage:F1}%\n{_monitorManager.LogicalCoreCount} cores ({(_settings.General.CombineLogicalCores ? "Combined" : "Individual")})";
                if (cpuTip != _lastTooltipCpu) { _lastTooltipCpu = cpuTip; PanelCpu.ToolTip = cpuTip; }

                double totalRamMb = _monitorManager.GetTotalRamMb();
                double usedRamMb = totalRamMb * (_monitorManager.RamUsage / 100.0);
                string ramTip = $"RAM: {_monitorManager.RamUsage:F1}%\n{usedRamMb / 1024:F1} / {totalRamMb / 1024:F1} GB";
                if (ramTip != _lastTooltipRam) { _lastTooltipRam = ramTip; PanelRam.ToolTip = ramTip; }

                string diskName = _settings.General.DiskInstanceName ?? "_Total";
                string diskTip = $"Disk: {diskName}\nRead: {_monitorManager.DiskReadUsage:F0}%\nWrite: {_monitorManager.DiskWriteUsage:F0}%";
                if (diskTip != _lastTooltipDisk) { _lastTooltipDisk = diskTip; PanelDisk.ToolTip = diskTip; }

                string netTip = $"Network\n↓ {FormatBytes(_monitorManager.NetDownload)}\n↑ {FormatBytes(_monitorManager.NetUpload)}";
                if (netTip != _lastTooltipNet) { _lastTooltipNet = netTip; PanelNet.ToolTip = netTip; }

                string gpuName = _hardwareMonitor is { IsAvailable: true, GpuName: not null } ? _hardwareMonitor.GpuName : "GPU";

                string dedicatedTip = FormatGpuMemoryTooltip(gpuName, "Dedicated VRam", _monitorManager.GpuDedicatedUsage,
                    _hardwareMonitor?.GpuDedicatedMemoryUsed, _hardwareMonitor?.GpuDedicatedMemoryTotal, _monitorManager.GpuDedicatedTotal);
                if (dedicatedTip != _lastTooltipGpuDedicated) { _lastTooltipGpuDedicated = dedicatedTip; PanelGpuDedicated.ToolTip = dedicatedTip; }

                string sharedTip = FormatGpuMemoryTooltip(gpuName, "Shared SRAM", _monitorManager.GpuSharedUsage,
                    _hardwareMonitor?.GpuSharedMemoryUsed, _hardwareMonitor?.GpuSharedMemoryTotal, _monitorManager.GpuSharedTotal);
                if (sharedTip != _lastTooltipGpuShared) { _lastTooltipGpuShared = sharedTip; PanelGpuShared.ToolTip = sharedTip; }
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateTooltips: {ex}"); }
        }

        private static string FormatGpuMemoryTooltip(
            string gpuName, string label, double usagePercentage,
            float? usedMb, float? totalMb, double totalBytes)
        {
            double usedBytes = (usedMb ?? 0) * 1024.0 * 1024.0;
            double total = 0;
            if ((totalMb ?? 0) > 0) total = (double)(totalMb ?? 0) * 1024.0 * 1024.0;
            else if (totalBytes > 0) total = totalBytes;

            double perc = 0;
            if (total > 0 && usedBytes > 0) perc = (usedBytes / total) * 100.0;
            else if (usagePercentage > 0) perc = usagePercentage;

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
