using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Interop;
using Microsoft.Win32;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfMessageBox = System.Windows.MessageBox;

namespace WinMeters
{
    /// <summary>
    /// Main window displaying system meters (CPU, RAM, Disk, Network).
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Monitors.MonitorManager _monitorManager;
        private Monitors.HardwareMonitorService? _hardwareMonitor;
        private DispatcherTimer? _timer;
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

        // Cache geometry for pie charts to avoid recreation
        private Geometry? _lastRamPieGeometry;
        private double _lastRamPercentage = -1;
        private Geometry? _lastGpuDedicatedGeometry;
        private double _lastGpuDedicatedPercentage = -1;
        private Geometry? _lastGpuSharedGeometry;
        private double _lastGpuSharedPercentage = -1;

        // Hotkey registration state (RegisterHotKey works system-wide, even in fullscreen games)
        private bool _hotkeyRegistered = false;
        private bool _fullscreenOverrideShow = false; // Tracks if user forced window visible in fullscreen

        // Foreground window tracking for Alt+Tab lowering mechanism
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private bool _isTopmostActive = true;

        public MainWindow()
        {
            InitializeComponent();
            _monitorManager = new Monitors.MonitorManager();

            _settings = AppSettings.Load();
            InitializeHardwareMonitor();
            ApplySettingsInternal();
            _settings.Save();

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
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
            InitializeWindowHeight();
            SetupCpuBars();
            RestoreWindowPosition();
            UpdateTooltips();

            // Initial visibility state
            if (_settings.Window.IsHiddenByUser)
            {
                this.Visibility = Visibility.Collapsed;
            }
        }

        private void InitializeWindowHeight()
        {
            if (_settings.Window.Height > 0)
            {
                this.Height = _settings.Window.Height;
            }
            else
            {
                double taskbarHeight = SystemParameters.PrimaryScreenHeight - SystemParameters.WorkArea.Height;
                if (taskbarHeight > 10)
                {
                    this.MinHeight = taskbarHeight;
                    this.Height = double.NaN;
                }
            }
        }

        private void RestoreWindowPosition()
        {
            if (_settings.Window.PositionX.HasValue && _settings.Window.PositionY.HasValue)
            {
                this.Left = _settings.Window.PositionX.Value;
                this.Top = _settings.Window.PositionY.Value;

                if (this.Left < SystemParameters.VirtualScreenLeft ||
                    this.Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Constants.Window.MinWindowVisibleWidth)
                {
                    CenterWindow();
                }
                if (this.Top < SystemParameters.VirtualScreenTop ||
                    this.Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Constants.Window.MinWindowVisibleHeight)
                {
                    CenterWindow();
                }
            }
            else
            {
                CenterWindow();
            }
        }

        private void CenterWindow()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            this.Left = (screenWidth - this.ActualWidth) / 2;
            this.Top = screenHeight - Constants.Window.DefaultWindowBottomOffset;
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
            _settings.Window.PositionX = this.Left;
            _settings.Window.PositionY = this.Top;
            _settings.Save();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                UnregisterHotkey();
                _timer?.Stop();
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
            source.AddHook(HwndHook);

            // Register system-wide hotkey for Ctrl+Alt+Shift+M using RegisterHotKey API
            // This works even when fullscreen games have focus
            RegisterHotkey();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == Constants.Hotkey.HotkeyId)
            {
                ToggleVisibility();
                handled = true;
            }
            return IntPtr.Zero;
        }

        #region System-Wide Hotkey (RegisterHotKey)

        private void RegisterHotkey()
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                var hwnd = helper.Handle;

                // Register Ctrl+Alt+Shift+M as a system-wide hotkey
                // MOD_CONTROL | MOD_ALT | MOD_SHIFT = 0x0007
                _hotkeyRegistered = NativeMethods.RegisterHotKey(
                    hwnd,
                    Constants.Hotkey.HotkeyId,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                    NativeMethods.VK_M);

                if (_hotkeyRegistered)
                {
                    WinMeters.Log.D("Ctrl+Alt+Shift+M hotkey registered successfully using RegisterHotKey API.");
                }
                else
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    WinMeters.Log.D($"Failed to register hotkey. Error code: {errorCode}. Another app may be using Ctrl+Alt+Shift+M.");
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"RegisterHotkey exception: {ex}");
            }
        }

        private void UnregisterHotkey()
        {
            try
            {
                if (_hotkeyRegistered)
                {
                    var helper = new WindowInteropHelper(this);
                    NativeMethods.UnregisterHotKey(helper.Handle, Constants.Hotkey.HotkeyId);
                    _hotkeyRegistered = false;
                    WinMeters.Log.D("Hotkey unregistered.");
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"UnregisterHotkey exception: {ex}");
            }
        }

        #endregion

        private void ToggleVisibility()
        {
            // Determine current actual visibility to toggle correctly in full-screen games
            bool currentlyVisible = this.Visibility == Visibility.Visible;

            if (currentlyVisible)
            {
                _settings.Window.IsHiddenByUser = true;
                _fullscreenOverrideShow = false;
                this.Visibility = Visibility.Collapsed;
            }
            else
            {
                _settings.Window.IsHiddenByUser = false;
                if (IsFullScreen())
                {
                    _fullscreenOverrideShow = true;
                }
                this.Visibility = Visibility.Visible;
                var toggleHelper = new WindowInteropHelper(this);
                SetWindowPosTopmost(toggleHelper.Handle);
                _isTopmostActive = true;
            }
            _settings.Save();
        }

        #endregion

        #region Menu Event Handlers

        private void MenuItem_About_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show(
                this,
                "WinMeters v2.4\n\n" +
                "A lightweight system monitoring utility for Windows.\n\n" +
                "Features:\n" +
                "• CPU & RAM monitoring\n" +
                "• Multi-GPU VRAM & SRAM tracking\n" +
                "• Disk & Network activity\n" +
                "• Hardware temperatures\n\n" +
                "• Use Ctrl+Alt+Shift+M to hide/show interface\n\n" +
                "Created with AI.",
                "About WinMeters",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        private void MenuItem_Startup_Click(object sender, RoutedEventArgs e)
        {
            _settings.General.StartWithWindows = MenuStartup.IsChecked;
            SetStartup(_settings.General.StartWithWindows);
            _settings.Save();
        }

        private void SetStartup(bool enable)
        {
            try
            {
                string? assemblyPath = Environment.ProcessPath
                    ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(assemblyPath)) return;

                string taskName = @"Custom Tasks\WinMeters";
                string args = enable
                    ? $"/Create /TN \"{taskName}\" /TR \"'{assemblyPath}'\" /SC ONLOGON /RL HIGHEST /F"
                    : $"/Delete /TN \"{taskName}\" /F";

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };

                System.Diagnostics.Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"SetStartup: {ex}");
            }
        }

        private void MenuItem_Reload_Click(object sender, RoutedEventArgs e)
        {
            ApplySettings();
        }

        private void MenuItem_EditSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_settings) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                ApplySettings();
            }
        }

        private void MenuItem_Lock_Click(object sender, RoutedEventArgs e)
        {
            _settings.Window.LockPosition = MenuLock.IsChecked;
            SavePosition();
        }

        private void MenuItem_Dock_Click(object sender, RoutedEventArgs e)
        {
            _settings.Window.DockOnTaskbar = MenuDock.IsChecked;
            if (_settings.Window.DockOnTaskbar) UpdateDockPosition();
            _settings.Save();
        }

        #endregion

        #region Dock Position

        private void UpdateDockPosition()
        {
            var workArea = SystemParameters.WorkArea;
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            if (workArea.Height < screenHeight)
            {
                this.Top = workArea.Top > 0 ? 0 : screenHeight - this.ActualHeight;
            }
            else if (workArea.Width < screenWidth)
            {
                this.Left = workArea.Left > 0 ? 0 : workArea.Right;
            }
        }

        #endregion

        #region Settings Application

        /// <summary>
        /// Loads settings from disk and applies them.
        /// </summary>
        public void ApplySettings()
        {
            _settings = AppSettings.Load();
            ClearCaches();
            ApplySettingsInternal();
        }

        /// <summary>
        /// Applies settings for live preview (does not save to disk).
        /// </summary>
        public void ApplySettingsLive(AppSettings settings)
        {
            _settings = settings;
            ClearCaches();
            ApplySettingsInternal();
        }

        private void ClearCaches()
        {
            _lastRamPieGeometry = null;
            _lastRamPercentage = -1;
            _lastGpuDedicatedGeometry = null;
            _lastGpuDedicatedPercentage = -1;
            _lastGpuSharedGeometry = null;
            _lastGpuSharedPercentage = -1;
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
                bgBrush.Opacity = _settings.General.Opacity;
                MainBorder.Background = bgBrush;
                MainBorder.BorderBrush = ColorHelper.ParseBrush(_settings.Colors.Border);
                MainBorder.BorderThickness = new Thickness(_settings.Colors.BorderThickness);

                // CPU
                SetupCpuBars();

                // RAM
                RamPie.Fill = ColorHelper.ParseBrush(_settings.Colors.RamPie);
                RamBorder.Stroke = ColorHelper.ParseBrush(_settings.Colors.RamBorder);
                RamBorder.StrokeThickness = _settings.Colors.RamBorderThickness;

                // GPU Dedicated
                GpuDedicatedPie.Fill = ColorHelper.ParseBrush(_settings.Colors.GpuDedicatedPie);
                GpuDedicatedBorder.Stroke = ColorHelper.ParseBrush(_settings.Colors.RamBorder); // Reusing RAM border color for consistency
                GpuDedicatedBorder.StrokeThickness = _settings.Colors.RamBorderThickness;

                // GPU Shared
                GpuSharedPie.Fill = ColorHelper.ParseBrush(_settings.Colors.GpuSharedPie);
                GpuSharedBorder.Stroke = ColorHelper.ParseBrush(_settings.Colors.RamBorder);
                GpuSharedBorder.StrokeThickness = _settings.Colors.RamBorderThickness;

                // Disk Labels
                DiskRestText.Foreground = ColorHelper.ParseBrush(_settings.Colors.DiskRead);
                DiskWriteLabel.Foreground = ColorHelper.ParseBrush(_settings.Colors.DiskWrite);
                DiskReadText.Foreground = WpfBrushes.White;
                DiskWriteText.Foreground = WpfBrushes.White;

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
            MenuLock.IsChecked = _settings.Window.LockPosition;
            MenuDock.IsChecked = _settings.Window.DockOnTaskbar;
            MenuStartup.IsChecked = _settings.General.StartWithWindows;
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
            if (!HandleFullScreenVisibility()) return;

            ManageForegroundWindowZOrder();
            long now = DateTime.UtcNow.Ticks;

            UpdateCpuMeters(now);
            UpdateRamMeter(now);
            UpdateDiskMeter(now);
            UpdateNetMeter(now);
            UpdateHardwareSensors(now);
            UpdateGpuMemoryMeters(now);
            UpdateTime(now);
            UpdateTooltips();

            if (_settings.Window.DockOnTaskbar)
                UpdateDockPosition();
        }

        /// <summary>
        /// Handles visibility during fullscreen applications.
        /// </summary>
        /// <returns>True if updates should continue, false if hidden.</returns>
        private bool HandleFullScreenVisibility()
        {
            if (_settings.Window.IsHiddenByUser)
            {
                if (this.Visibility != Visibility.Collapsed)
                {
                    this.Visibility = Visibility.Collapsed;
                }
                return false;
            }

            bool inFullScreen = IsFullScreen();
            if (!inFullScreen)
            {
                // Reset manual override when we are not in full screen
                _fullscreenOverrideShow = false;
            }

            if (inFullScreen && !_fullscreenOverrideShow)
            {
                if (this.Visibility == Visibility.Visible)
                {
                    this.Visibility = Visibility.Collapsed;
                }
                return false;
            }

            if (this.Visibility != Visibility.Visible)
            {
                this.Visibility = Visibility.Visible;
                var helper = new WindowInteropHelper(this);
                SetWindowPosTopmost(helper.Handle);
                _isTopmostActive = true;
            }
            return true;
        }

        /// <summary>
        /// When the user switches focus away from WinMeters (Alt+Tab, clicking another window),
        /// temporarily lower the window out of the topmost Z-order so it doesn't obscure the
        /// newly-focused application. When WinMeters regains focus or the user toggles it visible,
        /// restore topmost.
        /// </summary>
        private void ManageForegroundWindowZOrder()
        {
            if (this.Visibility != Visibility.Visible) return;

            IntPtr foregroundHwnd = NativeMethods.GetForegroundWindow();
            var helper = new WindowInteropHelper(this);
            IntPtr myHwnd = helper.Handle;

            // If the foreground window changed and it's not WinMeters itself, lower out of topmost
            if (foregroundHwnd != _lastForegroundHwnd && foregroundHwnd != myHwnd)
            {
                if (_isTopmostActive)
                {
                    SetWindowPosNonTopmost(helper.Handle);
                    _isTopmostActive = false;
                    WinMeters.Log.D($"WinMeters lowered from topmost (foreground: {foregroundHwnd:X})");
                }
            }
            else if (foregroundHwnd == myHwnd && !_isTopmostActive)
            {
                // User switched back to WinMeters — restore topmost
                SetWindowPosTopmost(helper.Handle);
                _isTopmostActive = true;
                WinMeters.Log.D("WinMeters restored to topmost (foreground: WinMeters)");
            }

            _lastForegroundHwnd = foregroundHwnd;
        }

        private void SetWindowPosTopmost(IntPtr hwnd)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        private void SetWindowPosNonTopmost(IntPtr hwnd)
        {
            // Place just below the topmost group (HWND_TOP = 0) — sits under TopMost windows but above regular ones
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
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
            UpdatePieWithCache(RamPie, _monitorManager.RamUsage, ref _lastRamPieGeometry, ref _lastRamPercentage);
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
            _monitorManager.UpdateGpu();

            // GpuDedicatedUsage / GpuSharedUsage are now percentages (0-100) from UpdateGpu().
            // Use HardwareMonitorService values as a more accurate fallback when available.
            if (IsReadyToUpdate(ref _lastGpuDedicatedTicks, _settings.Rates.GpuDedicated ?? _settings.General.RefreshRateMs, now))
            {
                double percentage = _monitorManager.GpuDedicatedUsage;

                // Prefer HardwareMonitorService derived percentage (more accurate for dedicated VRAM)
                if (_hardwareMonitor?.GpuDedicatedMemoryUsage.HasValue == true)
                    percentage = _hardwareMonitor.GpuDedicatedMemoryUsage.Value;

                UpdatePieWithCache(GpuDedicatedPie, percentage, ref _lastGpuDedicatedGeometry, ref _lastGpuDedicatedPercentage);
            }

            if (IsReadyToUpdate(ref _lastGpuSharedTicks, _settings.Rates.GpuShared ?? _settings.General.RefreshRateMs, now))
            {
                double percentage = _monitorManager.GpuSharedUsage;

                // Prefer HardwareMonitorService derived percentage (more accurate for shared SRAM)
                if (_hardwareMonitor?.GpuSharedMemoryUsage.HasValue == true)
                    percentage = _hardwareMonitor.GpuSharedMemoryUsage.Value;

                UpdatePieWithCache(GpuSharedPie, percentage, ref _lastGpuSharedGeometry, ref _lastGpuSharedPercentage);
            }
        }

        private void UpdateTime(long now)
        {
            if (!IsReadyToUpdate(ref _lastTimeTicks, 1000, now)) return;

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

        private static double GetGpuMemoryTotal(double? hardwareMonitorValueMb, double monitorManagerValueBytes)
        {
            double total = (hardwareMonitorValueMb ?? 0) * 1024.0 * 1024.0;
            return total > 0 ? total : monitorManagerValueBytes;
        }

        #endregion

        #region Tooltips

        private void UpdateTooltips()
        {
            try
            {
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
                PanelGpuDedicated.ToolTip = FormatGpuMemoryTooltip(gpuName, _monitorManager.GpuDedicatedUsage,
                    _hardwareMonitor?.GpuDedicatedMemoryTotal, _monitorManager.GpuDedicatedTotal);
                PanelGpuShared.ToolTip = FormatGpuMemoryTooltip(gpuName, _monitorManager.GpuSharedUsage,
                    _hardwareMonitor?.GpuSharedMemoryTotal, _monitorManager.GpuSharedTotal);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"UpdateTooltips: {ex}");
            }
        }

        private static string FormatGpuMemoryTooltip(string gpuName, double usedBytes, double? totalMb, double totalBytes)
        {
            double total = GetGpuMemoryTotal(totalMb, totalBytes);
            double perc = total > 0 ? (usedBytes / total) * 100.0 : 0;
            return $"{gpuName} Dedicated VRam: {perc:F1}%\n{usedBytes / GiB:F2} / {total / GiB:F2} GB";
        }

        private const double GiB = 1024.0 * 1024.0 * 1024.0;

        #endregion

        #region Pie Chart Updates

        private void UpdatePieWithCache(Path pieElement, double percentage, ref Geometry? cachedGeometry, ref double cachedPercentage)
        {
            if (Math.Abs(percentage - cachedPercentage) < 0.1)
            {
                if (cachedGeometry != null)
                    pieElement.Data = cachedGeometry;
                return;
            }

            cachedPercentage = percentage;
            cachedGeometry = CreatePieGeometry(percentage);
            pieElement.Data = cachedGeometry;
        }

        private Geometry CreatePieGeometry(double percentage)
        {
            double borderThickness = _settings.Colors.RamBorderThickness;
            double radius = Constants.Display.RamMeterRadius - (borderThickness / 2.0);
            if (radius < 0) radius = 0;

            double centerX = Constants.Display.RamMeterRadius;
            double centerY = Constants.Display.RamMeterRadius;

            if (percentage >= 100)
                return new EllipseGeometry(new WpfPoint(centerX, centerY), radius, radius);
            if (percentage <= 0)
                return new EllipseGeometry(new WpfPoint(centerX, centerY), 0, 0);

            double angle = (percentage / 100.0) * 360.0;
            double rad = (angle - 90) * Math.PI / 180.0;
            double x = centerX + radius * Math.Cos(rad);
            double y = centerY + radius * Math.Sin(rad);
            bool isLarge = angle > 180.0;

            var pathFig = new PathFigure { StartPoint = new WpfPoint(centerX, centerY) };
            pathFig.Segments.Add(new LineSegment(new WpfPoint(centerX, centerY - radius), false));
            pathFig.Segments.Add(new ArcSegment(new WpfPoint(x, y), new WpfSize(radius, radius), 0, isLarge, SweepDirection.Clockwise, false));
            pathFig.Segments.Add(new LineSegment(new WpfPoint(centerX, centerY), false));

            var geom = new PathGeometry();
            geom.Figures.Add(pathFig);
            geom.Freeze();
            return geom;
        }

        private void UpdatePie(Path pieElement, double percentage)
        {
            double borderThickness = _settings.Colors.RamBorderThickness;
            double radius = Constants.Display.RamMeterRadius - (borderThickness / 2.0);
            if (radius < 0) radius = 0;

            if (percentage >= 100)
            {
                pieElement.Data = new EllipseGeometry(new WpfPoint(12, 12), radius, radius);
                return;
            }
            if (percentage <= 0)
            {
                pieElement.Data = null;
                return;
            }

            double angle = (percentage / 100.0) * 360.0;
            double rad = (angle - 90) * Math.PI / 180.0;
            double x = 12 + radius * Math.Cos(rad);
            double y = 12 + radius * Math.Sin(rad);
            bool isLarge = angle > 180.0;

            var pathFig = new PathFigure { StartPoint = new WpfPoint(12, 12) };
            pathFig.Segments.Add(new LineSegment(new WpfPoint(12, 12 - radius), false));
            pathFig.Segments.Add(new ArcSegment(new WpfPoint(x, y), new WpfSize(radius, radius), 0, isLarge, SweepDirection.Clockwise, false));
            pathFig.Segments.Add(new LineSegment(new WpfPoint(12, 12), false));

            var geom = new PathGeometry();
            geom.Figures.Add(pathFig);
            pieElement.Data = geom;
        }

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

        private bool IsFullScreen()
        {
            return NativeMethods.SHQueryUserNotificationState(out NativeMethods.QUERY_USER_NOTIFICATION_STATE state) == 0 &&
                   (state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                    state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE);
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
