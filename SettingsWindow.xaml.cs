using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WnControls = System.Windows.Controls;
#if !DESIGN_TIME
using WnForms = System.Windows.Forms;
#endif

namespace WinMeters;

/// <summary>
/// Kil0bit-style Settings dialog. Five-section NavigationView (Home / General /
/// Monitoring / Appearance / About) over a 720x900 dark window. The structure is
/// hand-built in plain WPF -- no ModernWfp NuGet. Theme brushes and templates
/// come from Kil0bitTheme.xaml (merged in App.xaml).
///
/// The data flow is the same as the legacy single-page settings: a deep clone
/// of the caller's <see cref="AppSettings"/> is held in <c>_working</c>; on
/// Save, <c>_working</c> is copied back to <c>_original</c> and <c>_original</c>
/// is persisted. On cancel (window close without save), <c>_original</c> is
/// restored from <c>_snapshotBeforeEdit</c> so live-preview state can't leak
/// across an OK-then-Edit-again cycle. On Quit, both are persisted and the
/// application shuts down.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;
    private readonly AppSettings _snapshotBeforeEdit;

    private static readonly Dictionary<string, string> MeterDisplayNames = new()
    {
        ["Cpu"] = "CPU Usage",
        ["CpuTemp"] = "CPU Temp",
        ["GpuTemp"] = "GPU Temp",
        ["Ram"] = "RAM Usage",
        ["GpuDedicated"] = "GPU VRAM",
        ["GpuShared"] = "GPU SRAM",
        ["Disk"] = "Disk",
        ["Net"] = "Network",
        ["Time"] = "Time",
    };

    private readonly DispatcherTimer _liveUpdateTimer;
    private const int LiveUpdateDebounceMs = 120;

    private bool _isNavigating;

    public SettingsWindow(AppSettings original)
    {
        InitializeComponent();

        _original = original ?? throw new ArgumentNullException(nameof(original));

        var json = JsonSerializer.Serialize(original);
        _working = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        _snapshotBeforeEdit = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        _liveUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LiveUpdateDebounceMs) };
        _liveUpdateTimer.Tick += (s, e) =>
        {
            _liveUpdateTimer.Stop();
            ApplyChangesLive();
        };

        PopulateUi();
        SelectSection("Home");

        this.Closing += SettingsWindow_Closing;
    }

    // ---------------------------------------------------------------------
    // Section routing
    // ---------------------------------------------------------------------

    private void NavRail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WnControls.RadioButton rb && rb.Tag is string tag)
        {
            SelectSection(tag);
        }
    }

    private void HomeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WnControls.Button btn && btn.Tag is string tag)
        {
            SelectSection(tag);
        }
    }

    private void SelectSection(string sectionName)
    {
        if (string.IsNullOrEmpty(sectionName) || _isNavigating) return;
        _isNavigating = true;
        try
        {
            SectionHome.Visibility       = Visibility.Collapsed;
            SectionGeneral.Visibility    = Visibility.Collapsed;
            SectionMonitoring.Visibility = Visibility.Collapsed;
            SectionAppearance.Visibility = Visibility.Collapsed;
            SectionAbout.Visibility      = Visibility.Collapsed;

            switch (sectionName)
            {
                case "Home":       SectionHome.Visibility       = Visibility.Visible; break;
                case "General":    SectionGeneral.Visibility    = Visibility.Visible; break;
                case "Monitoring": SectionMonitoring.Visibility = Visibility.Visible; break;
                case "Appearance": SectionAppearance.Visibility = Visibility.Visible; break;
                case "About":      SectionAbout.Visibility      = Visibility.Visible; break;
            }

            // Sync the rail radio-button to the section we just rendered.
            WnControls.RadioButton? match = sectionName switch
            {
                "Home"       => NavHome,
                "General"    => NavGeneral,
                "Monitoring" => NavMonitoring,
                "Appearance" => NavAppearance,
                "About"      => NavAbout,
                _ => null
            };
            if (match is not null && match.IsChecked != true)
                match.IsChecked = true;
        }
        finally
        {
            _isNavigating = false;
        }
    }

    // ---------------------------------------------------------------------
    // UI population
    // ---------------------------------------------------------------------

    private void PopulateUi()
    {
        PopulateGeneralToggles();
        PopulateMonitoringToggles();
        PopulateAppearance();
        PopulateDisks();
        PopulateNetworkInterfaces();
        PopulateMeterOrder();
        PopulateAbout();
    }

    private void PopulateGeneralToggles()
    {
        ToggleLockPosition.IsChecked       = _working.Window.LockPosition;
        ToggleSnapToTaskbar.IsChecked      = _working.Window.StickToTaskbar;
        ToggleHideInFullscreen.IsChecked   = _working.General.HideInFullscreen;
        ToggleKeepOnTop.IsChecked          = _working.General.KeepOnTop;
        ToggleTime24H.IsChecked            = _working.General.Time24H;
        ToggleCombineLogicalCores.IsChecked = _working.General.CombineLogicalCores;

        // Refresh-rate combo: select the entry whose Tag matches the current
        // GlobalRefreshRateMs, falling back to the closest default for stale
        // values that pre-date the kil0bit-style preset list.
        int currentRate = _working.General.RefreshRateMs;
        int[] rateSteps = { 500, 1000, 2000, 5000 };
        int closest = rateSteps.OrderBy(v => Math.Abs(v - currentRate)).First();
        for (int i = 0; i < ComboRefreshRate.Items.Count; i++)
        {
            if (ComboRefreshRate.Items[i] is WnControls.ComboBoxItem item &&
                item.Tag is string tag &&
                int.TryParse(tag, out int v) && v == closest)
            {
                ComboRefreshRate.SelectedIndex = i;
                break;
            }
        }

        // Per-meter rates. SetRateText also wires the PreviewTextInput digit-only
        // filter (legacy behaviour) so the user can't paste non-digit characters
        // before ValidateRate catches them with an inline error.
        SetRateText(TxtRateCpu,          _working.Rates.Cpu);
        SetRateText(TxtRateRam,          _working.Rates.Ram);
        SetRateText(TxtRateDisk,         _working.Rates.Disk);
        SetRateText(TxtRateNet,          _working.Rates.Net);
        SetRateText(TxtRateCpuTemp,      _working.Rates.CpuTemp);
        SetRateText(TxtRateGpuTemp,      _working.Rates.GpuTemp);
        SetRateText(TxtRateGpuDedicated, _working.Rates.GpuDedicated);
        SetRateText(TxtRateGpuShared,    _working.Rates.GpuShared);
    }

    private void SetRateText(WnControls.TextBox tb, int? value)
    {
        tb.Text = (value ?? _working.General.RefreshRateMs).ToString();
        // Idempotent subscribe: detach-then-attach. CLR events cannot be
        // compared for nullness via == (CS0079); the cleanest "wire once" is
        // -= then += which is a no-op when the handler is absent. SetRateText
        // runs every PopulateUi (initial open + Reset All re-init or future
        // reload paths) so we MUSTN'T double-subscribe — two handlers on the
        // same TextBox would fire twice per keystroke and re-set e.Handled
        // twice (harmless but work doubling that compounds).
        tb.PreviewTextInput -= RateTextBox_PreviewTextInput;
        tb.PreviewTextInput += RateTextBox_PreviewTextInput;
    }

    private void RateTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        // Digit-only filter -- prevents accidental unit suffixes, signs, etc.
        // from landing in the TextBox before our ValidateRate catches them.
        // Matches the legacy single-page dialog filter exactly.
        e.Handled = !string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsDigit);
    }

    private void PopulateMonitoringToggles()
    {
        ToggleShowCpu.IsChecked          = _working.Visibility.ShowCpu;
        ToggleShowRam.IsChecked          = _working.Visibility.ShowRam;
        ToggleShowDisk.IsChecked         = _working.Visibility.ShowDisk;
        ToggleShowNet.IsChecked          = _working.Visibility.ShowNet;
        ToggleShowGpuDedicated.IsChecked = _working.Visibility.ShowGpuDedicated;
        ToggleShowGpuShared.IsChecked    = _working.Visibility.ShowGpuShared;
        ToggleShowCpuTemp.IsChecked      = _working.Visibility.ShowCpuTemp;
        ToggleShowGpuTemp.IsChecked      = _working.Visibility.ShowGpuTemp;
        ToggleShowHardwareLoad.IsChecked = _working.Visibility.ShowHardwareLoad;
        ToggleShowTime.IsChecked         = _working.Visibility.ShowTime;
    }

    private void PopulateAppearance()
    {
        SliderScale.Value   = _working.General.Scale;
        SliderOpacity.Value = _working.General.Opacity;

        // Each color swatch is a 20x20 Border. Brush is set in code so the swatch
        // can update in-place when the user picks a new colour (refreshes only
        // this single border; the wallpaper / dialog stays put).
        SetSwatch(SwatchAccent,         _working.Colors.Accent);
        SetSwatch(SwatchBackground,     _working.Colors.Background);
        SetSwatch(SwatchBorder,         _working.Colors.Border);
        SetSwatch(SwatchCpuSys,         _working.Colors.CpuSys);
        SetSwatch(SwatchCpuUser,        _working.Colors.CpuUser);
        SetSwatch(SwatchRamPie,         _working.Colors.RamPie);
        SetSwatch(SwatchRamBorder,      _working.Colors.RamBorder);
        SetSwatch(SwatchGpuDedicatedPie, _working.Colors.GpuDedicatedPie);
        SetSwatch(SwatchGpuSharedPie,   _working.Colors.GpuSharedPie);
        SetSwatch(SwatchCpuTemp,        _working.Colors.CpuTemp);
        SetSwatch(SwatchGpuTemp,        _working.Colors.GpuTemp);
        SetSwatch(SwatchDiskRead,       _working.Colors.DiskRead);
        SetSwatch(SwatchDiskWrite,      _working.Colors.DiskWrite);
        SetSwatch(SwatchNetDown,        _working.Colors.NetDown);
        SetSwatch(SwatchNetUp,          _working.Colors.NetUp);
        SetSwatch(SwatchTimeText,       _working.Colors.TimeText);
        SetSwatch(SwatchSeparator,      _working.Colors.Separator);
    }

    private void PopulateDisks()
    {
        try
        {
            using var mgr = new Monitors.MonitorManager();
            var disks = mgr.GetDiskInstances();
            ComboDisk.ItemsSource = disks;
            ComboDisk.SelectedItem = _working.General.DiskInstanceName;
            if (ComboDisk.SelectedIndex == -1 && ComboDisk.Items.Count > 0)
                ComboDisk.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.PopulateDisks: {ex}");
        }
    }

    private void PopulateNetworkInterfaces()
    {
        try
        {
            var interfaces = new List<string> { "(All Interfaces)" };
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                interfaces.Add(nic.Name);
            }
            ComboNetwork.ItemsSource = interfaces;
            var selected = string.IsNullOrWhiteSpace(_working.General.NetworkInterfaceName)
                ? "(All Interfaces)"
                : _working.General.NetworkInterfaceName;
            ComboNetwork.SelectedItem = selected;
            if (ComboNetwork.SelectedIndex == -1 && ComboNetwork.Items.Count > 0)
                ComboNetwork.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.PopulateNetworkInterfaces: {ex}");
        }
    }

    private void PopulateMeterOrder()
    {
        var items = new ObservableCollection<MeterOrderItem>();
        bool hwAdded = false;
        foreach (var key in _working.General.MeterOrder)
        {
            if (key is "CpuTemp" or "GpuTemp")
            {
                if (!hwAdded)
                {
                    items.Add(new MeterOrderItem { Key = "CpuTemp", Name = "CPU Temp" });
                    items.Add(new MeterOrderItem { Key = "GpuTemp", Name = "GPU Temp" });
                    hwAdded = true;
                }
            }
            else
            {
                items.Add(new MeterOrderItem
                {
                    Key = key,
                    Name = MeterDisplayNames.GetValueOrDefault(key, key)
                });
            }
        }
        ListMeterOrder.ItemsSource = items;
    }

    private void PopulateAbout()
    {
        try
        {
            string assemblyPath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(assemblyPath) && File.Exists(assemblyPath))
            {
                var info = FileVersionInfo.GetVersionInfo(assemblyPath);
                AboutVersion.Text = $"v{info.FileVersion}";
            }
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.PopulateAbout: {ex}");
        }
    }

    private static void SetSwatch(Border swatch, string hex)
    {
        swatch.Background = ColorHelper.ParseBrush(hex);
    }

    // ---------------------------------------------------------------------
    // Generic toggle / slider / combobox / textbox handlers
    // ---------------------------------------------------------------------

    private void GenericToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WnControls.CheckBox cb || cb.Tag is not string tag) return;
        bool value = cb.IsChecked == true;

        switch (tag)
        {
            case "LockPosition":         _working.Window.LockPosition = value; break;
            case "SnapToTaskbar":        _working.Window.StickToTaskbar = value; break;
            case "HideInFullscreen":     _working.General.HideInFullscreen = value; break;
            case "KeepOnTop":            _working.General.KeepOnTop = value; break;
            case "Time24H":              _working.General.Time24H = value; break;
            case "CombineLogicalCores":  _working.General.CombineLogicalCores = value; break;
            case "ShowCpu":              _working.Visibility.ShowCpu = value; break;
            case "ShowRam":              _working.Visibility.ShowRam = value; break;
            case "ShowDisk":             _working.Visibility.ShowDisk = value; break;
            case "ShowNet":              _working.Visibility.ShowNet = value; break;
            case "ShowGpuDedicated":     _working.Visibility.ShowGpuDedicated = value; break;
            case "ShowGpuShared":        _working.Visibility.ShowGpuShared = value; break;
            case "ShowCpuTemp":          _working.Visibility.ShowCpuTemp = value; break;
            case "ShowGpuTemp":          _working.Visibility.ShowGpuTemp = value; break;
            case "ShowHardwareLoad":     _working.Visibility.ShowHardwareLoad = value; break;
            case "ShowTime":             _working.Visibility.ShowTime = value; break;
        }
        TriggerLiveUpdate();
    }

    private void ComboRefreshRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboRefreshRate.SelectedItem is WnControls.ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out int ms))
        {
            _working.General.RefreshRateMs = ms;
            TriggerLiveUpdate();
        }
    }

    private void ComboDisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboDisk.SelectedItem is string sel)
        {
            _working.General.DiskInstanceName = sel;
            TriggerLiveUpdate();
        }
    }

    private void ComboNetwork_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboNetwork.SelectedItem is string sel)
        {
            _working.General.NetworkInterfaceName = (sel == "(All Interfaces)") ? null : sel;
            TriggerLiveUpdate();
        }
    }

    private void SliderScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _working.General.Scale = e.NewValue;
        TriggerLiveUpdate();
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _working.General.Opacity = e.NewValue;
        TriggerLiveUpdate();
    }

    private void RateTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not WnControls.TextBox tb || tb.Tag is not string tag) return;
        if (!ValidateRate(tb, GetErrorBlock(tag))) return;

        int? parsed = ParseNullableInt(tb.Text);
        switch (tag)
        {
            case "Cpu":          _working.Rates.Cpu = parsed; break;
            case "Ram":          _working.Rates.Ram = parsed; break;
            case "Disk":         _working.Rates.Disk = parsed; break;
            case "Net":          _working.Rates.Net = parsed; break;
            case "CpuTemp":      _working.Rates.CpuTemp = parsed; break;
            case "GpuTemp":      _working.Rates.GpuTemp = parsed; break;
            case "GpuDedicated": _working.Rates.GpuDedicated = parsed; break;
            case "GpuShared":    _working.Rates.GpuShared = parsed; break;
        }
        TriggerLiveUpdate();
    }

    private TextBlock? GetErrorBlock(string tag) => tag switch
    {
        "Cpu"          => ErrRateCpu,
        "Ram"          => ErrRateRam,
        "Disk"         => ErrRateDisk,
        "Net"          => ErrRateNet,
        "CpuTemp"      => ErrRateCpuTemp,
        "GpuTemp"      => ErrRateGpuTemp,
        "GpuDedicated" => ErrRateGpuDedicated,
        "GpuShared"    => ErrRateGpuShared,
        _              => null
    };

    private bool ValidateRate(WnControls.TextBox tb, TextBlock? err)
    {
        string s = tb.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(s))
        {
            ClearError(tb, err);
            return true;
        }
        if (!int.TryParse(s, out int v))
        {
            ShowError(tb, err, "Invalid number");
            return false;
        }
        if (v < Constants.Timing.MinValidationRateMs)
        {
            ShowError(tb, err, $"Minimum {Constants.Timing.MinValidationRateMs} ms");
            return false;
        }
        ClearError(tb, err);
        return true;
    }

    private void ShowError(WnControls.TextBox tb, TextBlock? err, string msg)
    {
        if (err is not null) { err.Text = msg; err.Visibility = Visibility.Visible; }
        tb.BorderBrush = (System.Windows.Media.Brush?)FindResource("ThemeDangerBrush");
        tb.ToolTip = msg;
    }

    private void ClearError(WnControls.TextBox tb, TextBlock? err)
    {
        if (err is not null) { err.Text = string.Empty; err.Visibility = Visibility.Collapsed; }
        tb.ClearValue(BorderBrushProperty);
        tb.ToolTip = null;
    }

    private static int? ParseNullableInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    // ---------------------------------------------------------------------
    // ListBox reorder + colour picker + hyperlink
    // ---------------------------------------------------------------------

    private void ListMeterOrder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnMoveUp.IsEnabled   = ListMeterOrder.SelectedIndex > 0;
        BtnMoveDown.IsEnabled = ListMeterOrder.SelectedIndex >= 0
                             && ListMeterOrder.SelectedIndex < ListMeterOrder.Items.Count - 1;
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (ListMeterOrder.ItemsSource is not ObservableCollection<MeterOrderItem> list) return;
        int idx = ListMeterOrder.SelectedIndex;
        if (idx > 0)
        {
            list.Move(idx, idx - 1);
            ListMeterOrder.SelectedIndex = idx - 1;
            TriggerLiveUpdate();
        }
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (ListMeterOrder.ItemsSource is not ObservableCollection<MeterOrderItem> list) return;
        int idx = ListMeterOrder.SelectedIndex;
        if (idx >= 0 && idx < list.Count - 1)
        {
            list.Move(idx, idx + 1);
            ListMeterOrder.SelectedIndex = idx + 1;
            TriggerLiveUpdate();
        }
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WnControls.Button btn || btn.Tag is not string tag) return;

        string currentHex = ResolveColorByTag(tag);
        Border? swatch = ResolveSwatchByTag(tag);
        if (swatch is null && tag != "Accent" && tag != "Background" && tag != "Border")
        {
            // Unknown tag, ignore.
            return;
        }

#if !DESIGN_TIME
        try
        {
            using var dlg = new WnForms.ColorDialog
            {
                FullOpen = true,
                Color = ColorHelper.ToDrawingColor(currentHex)
            };
            if (dlg.ShowDialog() != WnForms.DialogResult.OK) return;

            // Preserve the alpha byte from the existing hex if it had one (8-digit
            // ARGB), else default to opaque. The Background field normally carries
            // translucency so we keep the alpha explicit for background-only.
            string alpha = "FF";
            if (currentHex.Length == 9) alpha = currentHex.Substring(1, 2);
            else if (tag == "Background") alpha = "B4";

            string hex = $"#{alpha}{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";

            ApplyColorByTag(tag, hex);
            if (swatch is not null) SetSwatch(swatch, hex);
            TriggerLiveUpdate();
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.ColorButton_Click: {ex}");
        }
#endif
    }

    private string ResolveColorByTag(string tag) => tag switch
    {
        "Accent"          => _working.Colors.Accent,
        "Background"      => _working.Colors.Background,
        "Border"          => _working.Colors.Border,
        "CpuSys"          => _working.Colors.CpuSys,
        "CpuUser"         => _working.Colors.CpuUser,
        "RamPie"          => _working.Colors.RamPie,
        "RamBorder"       => _working.Colors.RamBorder,
        "GpuDedicatedPie" => _working.Colors.GpuDedicatedPie,
        "GpuSharedPie"    => _working.Colors.GpuSharedPie,
        "CpuTemp"         => _working.Colors.CpuTemp,
        "GpuTemp"         => _working.Colors.GpuTemp,
        "DiskRead"        => _working.Colors.DiskRead,
        "DiskWrite"       => _working.Colors.DiskWrite,
        "NetDown"         => _working.Colors.NetDown,
        "NetUp"           => _working.Colors.NetUp,
        "TimeText"        => _working.Colors.TimeText,
        "Separator"       => _working.Colors.Separator,
        _                 => "#FFFFFF"
    };

    private Border? ResolveSwatchByTag(string tag) => tag switch
    {
        "Accent"          => SwatchAccent,
        "Background"      => SwatchBackground,
        "Border"          => SwatchBorder,
        "CpuSys"          => SwatchCpuSys,
        "CpuUser"         => SwatchCpuUser,
        "RamPie"          => SwatchRamPie,
        "RamBorder"       => SwatchRamBorder,
        "GpuDedicatedPie" => SwatchGpuDedicatedPie,
        "GpuSharedPie"    => SwatchGpuSharedPie,
        "CpuTemp"         => SwatchCpuTemp,
        "GpuTemp"         => SwatchGpuTemp,
        "DiskRead"        => SwatchDiskRead,
        "DiskWrite"       => SwatchDiskWrite,
        "NetDown"         => SwatchNetDown,
        "NetUp"           => SwatchNetUp,
        "TimeText"        => SwatchTimeText,
        "Separator"       => SwatchSeparator,
        _                 => null
    };

    private void ApplyColorByTag(string tag, string hex)
    {
        switch (tag)
        {
            case "Accent":          _working.Colors.Accent = hex; break;
            case "Background":      _working.Colors.Background = hex; break;
            case "Border":          _working.Colors.Border = hex; break;
            case "CpuSys":          _working.Colors.CpuSys = hex; break;
            case "CpuUser":         _working.Colors.CpuUser = hex; break;
            case "RamPie":          _working.Colors.RamPie = hex; break;
            case "RamBorder":       _working.Colors.RamBorder = hex; break;
            case "GpuDedicatedPie": _working.Colors.GpuDedicatedPie = hex; break;
            case "GpuSharedPie":    _working.Colors.GpuSharedPie = hex; break;
            case "CpuTemp":         _working.Colors.CpuTemp = hex; break;
            case "GpuTemp":         _working.Colors.GpuTemp = hex; break;
            case "DiskRead":        _working.Colors.DiskRead = hex; break;
            case "DiskWrite":       _working.Colors.DiskWrite = hex; break;
            case "NetDown":         _working.Colors.NetDown = hex; break;
            case "NetUp":           _working.Colors.NetUp = hex; break;
            case "TimeText":        _working.Colors.TimeText = hex; break;
            case "Separator":       _working.Colors.Separator = hex; break;
        }
    }

    private void HyperlinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WnControls.Button btn && btn.Tag is string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HyperlinkButton_Click: {ex}");
            }
        }
    }

    // ---------------------------------------------------------------------
    // Footer handlers: Save, Reset, Quit
    // ---------------------------------------------------------------------

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // Surface validation errors before committing.
        if (GetErrorBlock("Cpu")?.Visibility == Visibility.Visible
         || GetErrorBlock("Ram")?.Visibility == Visibility.Visible
         || GetErrorBlock("Disk")?.Visibility == Visibility.Visible
         || GetErrorBlock("Net")?.Visibility == Visibility.Visible
         || GetErrorBlock("CpuTemp")?.Visibility == Visibility.Visible
         || GetErrorBlock("GpuTemp")?.Visibility == Visibility.Visible
         || GetErrorBlock("GpuDedicated")?.Visibility == Visibility.Visible
         || GetErrorBlock("GpuShared")?.Visibility == Visibility.Visible)
        {System.Windows.MessageBox.Show(this,
                $"One or more refresh rates are invalid. Minimum is {Constants.Timing.MinValidationRateMs} ms; fix the highlighted values before saving.",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ApplyMeterOrderToWorking();
        CopyWorkingToOriginal();
        _original.Save();
        DialogResult = true;
        Close();
    }

    private void BtnResetAll_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "Reset all settings to factory defaults?\n\nThis will revert general, monitoring, appearance, color, and rate preferences. The action cannot be undone.",
            "Reset All Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var defaults = new AppSettings();
        // Fold every category from defaults into _working.
        _working.General    = defaults.General;
        _working.Window     = defaults.Window;
        _working.Colors     = defaults.Colors;
        _working.Visibility = defaults.Visibility;
        _working.Rates      = defaults.Rates;

        PopulateUi();
        TriggerLiveUpdate();
    }

    private void BtnQuit_Click(object sender, RoutedEventArgs e)
    {
        BtnSave_Click(sender, e); // persist first
        System.Windows.Application.Current.Shutdown();
    }

    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If the user clicked the X (no Save), revert any live-preview state so a
        // follow-up OK doesn't see mutated values. DialogResult is set to false
        // here so the consumer can distinguish "saved" vs "cancelled".
        if (DialogResult == true) return;

        // Cancel / close without saving -> restore from snapshot.
        try
        {
            var restored = JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(_snapshotBeforeEdit)) ?? new AppSettings();
            _original.General    = restored.General;
            _original.Window     = restored.Window;
            _original.Colors     = restored.Colors;
            _original.Visibility = restored.Visibility;
            _original.Rates      = restored.Rates;
            if (Owner is MainWindow mw) mw.ApplySettingsLive(_original);
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow cancel-revert: {ex}");
        }
    }

    private void ApplyMeterOrderToWorking()
    {
        if (ListMeterOrder.ItemsSource is not ObservableCollection<MeterOrderItem> list) return;
        var newOrder = new List<string>();
        foreach (var item in list) newOrder.Add(item.Key);
        _working.General.MeterOrder = newOrder;
    }

    private void CopyWorkingToOriginal()
    {
        _original.General    = _working.General;
        _original.Window     = _working.Window;
        _original.Colors     = _working.Colors;
        _original.Visibility = _working.Visibility;
        _original.Rates      = _working.Rates;
    }

    private void TriggerLiveUpdate()
    {
        _liveUpdateTimer.Stop();
        _liveUpdateTimer.Start();
    }

    private void ApplyChangesLive()
    {
        if (!IsLoaded) return;
        // Push the working clone into the live state and let MainWindow apply.
        CopyWorkingToOriginal();
        if (Owner is MainWindow mw) mw.ApplySettingsLive(_original);
    }

    private class MeterOrderItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
