using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WnControls = System.Windows.Controls;
using WnShapes = System.Windows.Shapes;
#if !DESIGN_TIME
using WnForms = System.Windows.Forms;
using WnDrawing = System.Drawing;
#endif

namespace WinMeters;

/// <summary>
/// Settings dialog window for configuring WinMeters appearance and behavior.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;
    private readonly AppSettings _snapshotBeforeEdit;

    private static readonly Dictionary<string, string> FriendlyNames = new()
    {
        ["Cpu"] = "CPU Usage",
        ["Ram"] = "Total RAM Usage",
        ["Disk"] = "Disk Activity",
        ["Net"] = "Network Activity",
        ["H/W Temps"] = "H/W Temperatures",
        ["GpuDedicated"] = "GPU VRAM Usage",
        ["GpuShared"] = "GPU SRAM Usage",
        ["Time"] = "System Time"
    };

    // Debounce timer for live updates to avoid excessive work while the user is dragging.
    private System.Windows.Threading.DispatcherTimer? _liveUpdateTimer;
    private const int LiveUpdateDebounceMs = 100;

    public SettingsWindow(AppSettings original)
    {
        InitializeComponent();
        _original = original ?? throw new ArgumentNullException(nameof(original));

        // Deep clone via JSON roundtrip so we never mutate the caller's settings until OK.
        var json = JsonSerializer.Serialize(original);
        _working = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        _snapshotBeforeEdit = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        DataContext = _working;
        SetupLiveUpdateDebounce();
        PopulateUi();
        // The window is closed-and-discarded; the GC will reclaim the handlers naturally,
        // so no manual -=/Unsubscribe work is needed here.
    }

    private void SetupLiveUpdateDebounce()
    {
        _liveUpdateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LiveUpdateDebounceMs)
        };
        _liveUpdateTimer.Tick += (s, e) =>
        {
            _liveUpdateTimer?.Stop();
            ApplyChangesLive();
        };
    }

    private void TriggerLiveUpdate()
    {
        _liveUpdateTimer?.Stop();
        _liveUpdateTimer?.Start();
    }

    private void PopulateUi()
    {
        PopulateSliders();
        PopulateVisibilityCheckboxes();
        PopulateRateTextboxes();
        PopulateColors();
        PopulateDisks();
        PopulateNetworkInterfaces();
        PopulateMeterOrder();
    }

    private void PopulateSliders()
    {
        SliderOpacity.Value = _working.General.Opacity;
        TxtOpacity.Text = _working.General.Opacity.ToString("F2");
        SliderOpacity.ValueChanged += (s, e) =>
        {
            TxtOpacity.Text = SliderOpacity.Value.ToString("F2");
            TriggerLiveUpdate();
        };

        SliderScale.Value = _working.General.Scale;
        TxtScale.Text = _working.General.Scale.ToString("F2");
        SliderScale.ValueChanged += (s, e) =>
        {
            TxtScale.Text = SliderScale.Value.ToString("F2");
            TriggerLiveUpdate();
        };
    }

    private void PopulateVisibilityCheckboxes()
    {
        // Single check handler is shared — null check is suppressed by the cast pattern below.
        ChkCpu.IsChecked = _working.Visibility.ShowCpu;
        ChkRam.IsChecked = _working.Visibility.ShowRam;
        ChkDisk.IsChecked = _working.Visibility.ShowDisk;
        ChkNet.IsChecked = _working.Visibility.ShowNet;
        ChkCpuTemp.IsChecked = _working.Visibility.ShowCpuTemp;
        ChkGpuTemp.IsChecked = _working.Visibility.ShowGpuTemp;
        ChkHardwareLoad.IsChecked = _working.Visibility.ShowHardwareLoad;
        ChkGpuDedicated.IsChecked = _working.Visibility.ShowGpuDedicated;
        ChkGpuShared.IsChecked = _working.Visibility.ShowGpuShared;
        ChkCombineCpu.IsChecked = _working.General.CombineLogicalCores;
        ChkTime.IsChecked = _working.Visibility.ShowTime;
        ChkTime24H.IsChecked = _working.General.Time24H;

        RoutedEventHandler checkHandler = (s, e) => TriggerLiveUpdate();
        foreach (var chk in new[] { ChkCpu, ChkRam, ChkDisk, ChkNet, ChkCpuTemp, ChkGpuTemp, ChkHardwareLoad, ChkGpuDedicated, ChkGpuShared, ChkCombineCpu, ChkTime, ChkTime24H })
        {
            chk.Checked += checkHandler;
            chk.Unchecked += checkHandler;
        }
    }

    private void PopulateRateTextboxes()
    {
        var rateMap = new Dictionary<WnControls.TextBox, string>
        {
            { TxtRateCpu, nameof(_working.Rates.Cpu) },
            { TxtRateRam, nameof(_working.Rates.Ram) },
            { TxtRateDisk, nameof(_working.Rates.Disk) },
            { TxtRateNet, nameof(_working.Rates.Net) },
            { TxtRateCpuTemp, nameof(_working.Rates.CpuTemp) },
            { TxtRateGpuTemp, nameof(_working.Rates.GpuTemp) },
            { TxtRateGpuDedicated, nameof(_working.Rates.GpuDedicated) },
            { TxtRateGpuShared, nameof(_working.Rates.GpuShared) }
        };

        // Set initial values
        foreach (var (tb, prop) in rateMap)
        {
            var value = prop switch
            {
                nameof(_working.Rates.Cpu) => _working.Rates.Cpu ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.Ram) => _working.Rates.Ram ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.Disk) => _working.Rates.Disk ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.Net) => _working.Rates.Net ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.CpuTemp) => _working.Rates.CpuTemp ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.GpuTemp) => _working.Rates.GpuTemp ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.GpuDedicated) => _working.Rates.GpuDedicated ?? _working.General.RefreshRateMs,
                nameof(_working.Rates.GpuShared) => _working.Rates.GpuShared ?? _working.General.RefreshRateMs,
                _ => _working.General.RefreshRateMs
            };
            tb.Text = value.ToString();
        }

        // Setup validation and input filtering
        var textChangedHandler = new TextChangedEventHandler((s, e) =>
        {
            if (s is WnControls.TextBox tb)
            {
                var errorBlock = tb.Parent is WnControls.StackPanel panel && panel.Children.Count >= 3
                    ? panel.Children[2] as TextBlock
                    : null;
                if (ValidateRate(tb, errorBlock))
                    TriggerLiveUpdate();
            }
        });

        var previewTextHandler = new TextCompositionEventHandler((s, e) =>
        {
            e.Handled = !string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsDigit);
        });

        foreach (var tb in rateMap.Keys)
        {
            tb.TextChanged += textChangedHandler;
            tb.PreviewTextInput += previewTextHandler;
        }

        // Setup error display references
        SetupRateError(TxtRateCpu, ErrRateCpu);
        SetupRateError(TxtRateRam, ErrRateRam);
        SetupRateError(TxtRateDisk, ErrRateDisk);
        SetupRateError(TxtRateNet, ErrRateNet);
        SetupRateError(TxtRateCpuTemp, ErrRateCpuTemp);
        SetupRateError(TxtRateGpuTemp, ErrRateGpuTemp);
        SetupRateError(TxtRateGpuDedicated, ErrRateGpuDedicated);
        SetupRateError(TxtRateGpuShared, ErrRateGpuShared);

        ValidateAll();
    }

    private void SetupRateError(WnControls.TextBox tb, TextBlock err)
    {
        // Store error reference for validation
        tb.Tag = err;
    }

    private void PopulateColors()
    {
        ColorsPanel.Children.Clear();

        var colorProperties = new[]
        {
            ("Background", (Action<string>)(v => _working.Colors.Background = v)),
            ("Border", (Action<string>)(v => _working.Colors.Border = v)),
            ("CpuSys", (Action<string>)(v => _working.Colors.CpuSys = v)),
            ("CpuUser", (Action<string>)(v => _working.Colors.CpuUser = v)),
            ("RAM", (Action<string>)(v => _working.Colors.RamPie = v)),
            ("RamBorder", (Action<string>)(v => _working.Colors.RamBorder = v)),
            ("VRAM", (Action<string>)(v => _working.Colors.GpuDedicatedPie = v)),
            ("SRAM", (Action<string>)(v => _working.Colors.GpuSharedPie = v)),
            ("CpuTemp", (Action<string>)(v => _working.Colors.CpuTemp = v)),
            ("GpuTemp", (Action<string>)(v => _working.Colors.GpuTemp = v)),
            ("DiskRead", (Action<string>)(v => _working.Colors.DiskRead = v)),
            ("DiskWrite", (Action<string>)(v => _working.Colors.DiskWrite = v)),
            ("NetDown", (Action<string>)(v => _working.Colors.NetDown = v)),
            ("NetUp", (Action<string>)(v => _working.Colors.NetUp = v)),
            ("Time", (Action<string>)(v => _working.Colors.TimeText = v))
        };

        foreach (var (name, setter) in colorProperties)
        {
            AddColorEditor(name, setter);
        }
    }

    private void AddColorEditor(string name, Action<string> setter)
    {
        string GetHex() => name switch
        {
            "Background" => _working.Colors.Background,
            "Border" => _working.Colors.Border,
            "CpuSys" => _working.Colors.CpuSys,
            "CpuUser" => _working.Colors.CpuUser,
            "RAM" => _working.Colors.RamPie,
            "RamBorder" => _working.Colors.RamBorder,
            "VRAM" => _working.Colors.GpuDedicatedPie,
            "SRAM" => _working.Colors.GpuSharedPie,
            "CpuTemp" => _working.Colors.CpuTemp,
            "GpuTemp" => _working.Colors.GpuTemp,
            "DiskRead" => _working.Colors.DiskRead,
            "DiskWrite" => _working.Colors.DiskWrite,
            "NetDown" => _working.Colors.NetDown,
            "NetUp" => _working.Colors.NetUp,
            "Time" => _working.Colors.TimeText,
            _ => "#000000"
        };

        var panel = new StackPanel
        {
            Width = 200,
            Margin = new Thickness(4),
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };

        panel.Children.Add(new TextBlock
        {
            Text = name,
            Width = 60,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10
        });

        var rect = new WnShapes.Rectangle
        {
            Width = 20,
            Height = 20,
            Stroke = System.Windows.Media.Brushes.Black,
            StrokeThickness = 1,
            Margin = new Thickness(6, 0, 6, 0),
            Fill = ColorHelper.ParseBrush(GetHex()),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        rect.MouseLeftButtonUp += (s, e) => OpenColorPicker(rect, setter, GetHex);
        panel.Children.Add(rect);

        var txt = new TextBlock
        {
            Text = GetHex(),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            MinWidth = 70
        };
        panel.Children.Add(txt);

        ColorsPanel.Children.Add(panel);
    }

    private void OpenColorPicker(WnShapes.Rectangle rect, Action<string> setter, Func<string> getCurrentHex)
    {
        try
        {
#if !DESIGN_TIME
            using var dlg = new WnForms.ColorDialog
            {
                Color = ColorHelper.ToDrawingColor(getCurrentHex()),
                FullOpen = true
            };

            if (dlg.ShowDialog() == WnForms.DialogResult.OK)
            {
                var hex = ColorHelper.ToHexString(dlg.Color);
                setter(hex);
                rect.Fill = ColorHelper.FromDrawingColor(dlg.Color);

                // Update the text block
                if (rect.Parent is StackPanel parent && parent.Children.Count >= 3)
                {
                    (parent.Children[2] as TextBlock)?.SetCurrentValue(TextBlock.TextProperty, hex);
                }

                TriggerLiveUpdate();
            }
#else
            rect.Fill = ColorHelper.ParseBrush(getCurrentHex());
#endif
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.OpenColorPicker: {ex}");
        }
    }

    private void PopulateDisks()
    {
        try
        {
            using var mgr = new Monitors.MonitorManager();
            var disks = mgr.GetDiskInstances();

            ComboDisk.ItemsSource = disks;
            SelectComboItem(ComboDisk, _working.General.DiskInstanceName);

            ComboDisk.SelectionChanged += (s, e) =>
            {
                if (ComboDisk.SelectedItem is string sel)
                {
                    _working.General.DiskInstanceName = sel;
                    TriggerLiveUpdate();
                }
            };
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"PopulateDisks: {ex}");
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

            var selectedNet = string.IsNullOrWhiteSpace(_working.General.NetworkInterfaceName)
                ? "(All Interfaces)"
                : _working.General.NetworkInterfaceName;
            SelectComboItem(ComboNetwork, selectedNet);

            ComboNetwork.SelectionChanged += (s, e) =>
            {
                if (ComboNetwork.SelectedItem is string sel)
                {
                    _working.General.NetworkInterfaceName =
                        (sel == "(All Interfaces)") ? null : sel;
                    TriggerLiveUpdate();
                }
            };
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"PopulateNetworkInterfaces: {ex}");
        }
    }

    private void SelectComboItem(WnControls.ComboBox combo, string? value)
    {
        combo.SelectedItem = value;
        if (combo.SelectedIndex == -1 && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private void PopulateMeterOrder()
    {
        var displayItems = new ObservableCollection<MeterOrderItem>();
        bool hwAdded = false;

        foreach (var key in _working.General.MeterOrder)
        {
            if (key is "CpuTemp" or "GpuTemp")
            {
                if (!hwAdded)
                {
                    displayItems.Add(new MeterOrderItem { Key = "H/W Temps", Name = FriendlyNames["H/W Temps"] });
                    hwAdded = true;
                }
            }
            else
            {
                displayItems.Add(new MeterOrderItem
                {
                    Key = key,
                    Name = FriendlyNames.GetValueOrDefault(key, key)
                });
            }
        }

        ListMeterOrder.ItemsSource = displayItems;
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        int index = ListMeterOrder.SelectedIndex;
        if (index > 0 && ListMeterOrder.ItemsSource is ObservableCollection<MeterOrderItem> list)
        {
            list.Move(index, index - 1);
            TriggerLiveUpdate();
        }
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        int index = ListMeterOrder.SelectedIndex;
        if (ListMeterOrder.ItemsSource is ObservableCollection<MeterOrderItem> list
            && index >= 0 && index < list.Count - 1)
        {
            list.Move(index, index + 1);
            TriggerLiveUpdate();
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateAll())
        {
            System.Windows.MessageBox.Show(
                this,
                $"One or more refresh rates are invalid. Fix the highlighted values (minimum {Constants.Timing.MinValidationRateMs} ms) before saving.",
                "Validation Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        ApplyValuesToWorking();
        CopyWorkingToOriginal();
        _original.Save();

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Restore the original settings from the snapshot via a JSON roundtrip so any
            // future code path that ends up aliasing _working and _snapshotBeforeEdit sub-objects
            // (e.g. via a live-preview setter that mutates an inner list in place) cannot silently
            // keep preview state. The roundtrip guarantees `restored` has fresh, independent
            // sub-references that the assignment below cannot be tripped up by.
            var restored = JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(_snapshotBeforeEdit)) ?? new AppSettings();

            _original.General    = restored.General;
            _original.Window     = restored.Window;
            _original.Colors     = restored.Colors;
            _original.Visibility = restored.Visibility;
            _original.Rates      = restored.Rates;

            if (Owner is MainWindow mw)
                mw.ApplySettingsLive(_original);
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.CancelRevert: {ex}");
        }

        DialogResult = false;
        Close();
    }

    private void ApplyChangesLive()
    {
        if (!ValidateAll()) return;

        ApplyValuesToWorking();
        CopyWorkingToOriginal();

        if (Owner is MainWindow mw)
        {
            // ApplySettingsLive internally calls ApplyWindowMode, so we don't need
            // a separate explicit call here — that would issue a duplicate
            // ABM_REMOVE / ABM_NEW round-trip on every dropdown tick in AppBar mode.
            mw.ApplySettingsLive(_original);
        }
    }

    private void ApplyValuesToWorking()
    {
        _working.General.Opacity = SliderOpacity.Value;
        _working.General.Scale = SliderScale.Value;

        _working.Rates.Cpu = ParseNullableInt(TxtRateCpu.Text);
        _working.Rates.Ram = ParseNullableInt(TxtRateRam.Text);
        _working.Rates.Disk = ParseNullableInt(TxtRateDisk.Text);
        _working.Rates.Net = ParseNullableInt(TxtRateNet.Text);
        _working.Rates.CpuTemp = ParseNullableInt(TxtRateCpuTemp.Text);
        _working.Rates.GpuTemp = ParseNullableInt(TxtRateGpuTemp.Text);
        _working.Rates.GpuDedicated = ParseNullableInt(TxtRateGpuDedicated.Text);
        _working.Rates.GpuShared = ParseNullableInt(TxtRateGpuShared.Text);

        _working.Visibility.ShowCpu = ChkCpu.IsChecked == true;
        _working.Visibility.ShowRam = ChkRam.IsChecked == true;
        _working.Visibility.ShowDisk = ChkDisk.IsChecked == true;
        _working.Visibility.ShowNet = ChkNet.IsChecked == true;
        _working.Visibility.ShowCpuTemp = ChkCpuTemp.IsChecked == true;
        _working.Visibility.ShowGpuTemp = ChkGpuTemp.IsChecked == true;
        _working.Visibility.ShowHardwareLoad = ChkHardwareLoad.IsChecked == true;
        _working.Visibility.ShowGpuDedicated = ChkGpuDedicated.IsChecked == true;
        _working.Visibility.ShowGpuShared = ChkGpuShared.IsChecked == true;
        _working.General.CombineLogicalCores = ChkCombineCpu.IsChecked == true;
        _working.Visibility.ShowTime = ChkTime.IsChecked == true;
        _working.General.Time24H = ChkTime24H.IsChecked == true;

        if (ListMeterOrder.ItemsSource is ObservableCollection<MeterOrderItem> list)
        {
            var newOrder = new List<string>();
            foreach (var item in list)
            {
                if (item.Key == "H/W Temps")
                {
                    newOrder.Add("CpuTemp");
                    newOrder.Add("GpuTemp");
                }
                else
                {
                    newOrder.Add(item.Key);
                }
            }
            _working.General.MeterOrder = newOrder;
        }
    }

    private static int? ParseNullableInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    private bool ValidateRate(WnControls.TextBox tb, TextBlock? err)
    {
        var s = tb.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(s))
        {
            ClearError(tb, err);
            return true;
        }

        if (!int.TryParse(s, out var v))
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

    private bool ValidateAll()
    {
        return
            ValidateRate(TxtRateCpu, ErrRateCpu) &&
            ValidateRate(TxtRateRam, ErrRateRam) &&
            ValidateRate(TxtRateDisk, ErrRateDisk) &&
            ValidateRate(TxtRateNet, ErrRateNet) &&
            ValidateRate(TxtRateCpuTemp, ErrRateCpuTemp) &&
            ValidateRate(TxtRateGpuTemp, ErrRateGpuTemp) &&
            ValidateRate(TxtRateGpuDedicated, ErrRateGpuDedicated) &&
            ValidateRate(TxtRateGpuShared, ErrRateGpuShared);
    }

    private void ShowError(WnControls.TextBox tb, TextBlock? err, string message)
    {
        if (err is not null) err.Text = message;
        if (err is not null) err.Visibility = Visibility.Visible;
        tb.BorderBrush = System.Windows.Media.Brushes.Red;
        tb.ToolTip = message;
    }

    private void ClearError(WnControls.TextBox tb, TextBlock? err)
    {
        if (err is not null) err.Text = string.Empty;
        if (err is not null) err.Visibility = Visibility.Collapsed;
        tb.ClearValue(BorderBrushProperty);
        tb.ToolTip = null;
    }

    private void CopyWorkingToOriginal()
    {
        _original.General = _working.General;
        _original.Window = _working.Window;
        _original.Colors = _working.Colors;
        _original.Visibility = _working.Visibility;
        _original.Rates = _working.Rates;
    }

    // Helper class for meter order display
    private class MeterOrderItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
