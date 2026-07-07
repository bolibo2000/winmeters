using System.Collections.ObjectModel;
using System.Globalization;
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
    // Compat shims preserved from the modernized SettingsWindow: MainWindow.OpenSettingsAndNavigateTo
    // opens this dialog modeless via Show() and reads WasSaved on Closed to decide whether to
    // ApplySettings, plus calls SelectSection("About") from the RMB-tray About entry. The single-
    // page .WM.old layout has no nav-rail sections, so SelectSection is a no-op. WasSaved is
    // flipped to true in BtnOk_Click right before Close(); BtnCancel_Click keeps it at the
    // default false (the explicit set is defensive). The legacy DialogResult setter is removed
    // below because it InvalidOperationException-throws on modeless Show()'d windows and the
    // MainWindow pair-up doesn't read DialogResult anyway (replaced by WasSaved here).

    /// <summary>True iff the user clicked OK and the dialog committed its edits to _original.</summary>
    public bool WasSaved { get; private set; }

    /// <summary>
    /// No-op in the single-page .WM.old layout -- the modernized version had a 4-section nav
    /// rail (Home / General / Monitoring / Appearance) that this dialog has no equivalent for.
    /// Kept as a compat shim so the tray-icon RMB About entry still resolves on
    /// MainWindow.OpenSettingsAndNavigateTo without throwing on a missing method.
    /// </summary>
    public void SelectSection(string sectionName) { /* single-page dialog has no sections */ }

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

    // Track event handlers for cleanup
    private readonly List<RoutedPropertyChangedEventHandler<double>> _sliderValueHandlers = new();
    private readonly List<RoutedEventHandler> _checkboxHandlers = new();
    private readonly List<TextChangedEventHandler> _rateTextChangedHandlers = new();
    private readonly List<TextCompositionEventHandler> _ratePreviewTextHandlers = new();
    // PopulateDisks / PopulateNetworkInterfaces SelectionChanged lambdas tracked
    // separately so UnsubscribeDialogHandlers can detach them on close AND
    // before a Reset-driven re-PopulateUi. Without this list pattern they
    // would multiply one extra subscription per Reset (the old lambda stays
    // attached to the ComboBox, and on every future user interaction it fires
    // alongside the new lambda and calls TriggerLiveUpdate through the
    // rebroadcast old closure). Mirrors the existing per-list pattern.
    private readonly List<SelectionChangedEventHandler> _diskComboHandlers = new();
    private readonly List<SelectionChangedEventHandler> _nicComboHandlers  = new();

    // Debounce timer for live updates to avoid excessive processing
    private System.Windows.Threading.DispatcherTimer? _liveUpdateTimer;
    private const int LiveUpdateDebounceMs = 100;

    public SettingsWindow(AppSettings original)
    {
        InitializeComponent();
        _original = original ?? throw new ArgumentNullException(nameof(original));

        // Deep clone to avoid mutating original until user confirms
        var json = JsonSerializer.Serialize(original);
        _working = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        _snapshotBeforeEdit = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        DataContext = _working;
        SetupLiveUpdateDebounce();
        PopulateUi();

        this.Closed += SettingsWindow_Closed;
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

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        // Live-update debounce timer is a no-op once the window is gone;
        // DispatcherTimer doesn't implement IDisposable in WPF.
        _liveUpdateTimer?.Stop();

        // Memory-leak prevention: drop every per-control handler we subscribed
        // during PopulateUi. Extracted to UnsubscribeDialogHandlers so
        // BtnReset_Click can re-use the same cleanup before re-populating
        // against a default-constructed _working instance.
        UnsubscribeDialogHandlers();
    }

    /// <summary>
    /// Removes every event handler we subscribed during PopulateUi (sliders /
    /// visibility checkboxes / refresh-rate textboxes / refresh-rate preview)
    /// and clears the tracking lists so a follow-up PopulateUi starts from a
    /// clean slate. Called from BOTH SettingsWindow_Closed (memory-leak
    /// prevention) AND BtnReset_Click (so the old handlers don't double-fire
    /// alongside the new ones after _working is swapped for defaults).
    /// </summary>
    private void UnsubscribeDialogHandlers()
    {
        foreach (var handler in _sliderValueHandlers)
        {
            SliderOpacity.ValueChanged -= handler;
            SliderScale.ValueChanged -= handler;
        }
        _sliderValueHandlers.Clear();

        foreach (var handler in _checkboxHandlers)
        {
            foreach (var chk in new[] { ChkCpu, ChkRam, ChkDisk, ChkNet, ChkCpuTemp, ChkGpuTemp, ChkHardwareLoad, ChkGpuDedicated, ChkGpuShared, ChkCombineCpu, ChkTime, ChkTime24H })
            {
                chk.Checked -= handler;
                chk.Unchecked -= handler;
            }
        }
        _checkboxHandlers.Clear();

        foreach (var handler in _rateTextChangedHandlers)
        {
            TxtRateCpu.TextChanged -= handler;
            TxtRateRam.TextChanged -= handler;
            TxtRateDisk.TextChanged -= handler;
            TxtRateNet.TextChanged -= handler;
            TxtRateCpuTemp.TextChanged -= handler;
            TxtRateGpuTemp.TextChanged -= handler;
            TxtRateGpuDedicated.TextChanged -= handler;
            TxtRateGpuShared.TextChanged -= handler;
        }
        _rateTextChangedHandlers.Clear();

        foreach (var handler in _ratePreviewTextHandlers)
        {
            TxtRateCpu.PreviewTextInput -= handler;
            TxtRateRam.PreviewTextInput -= handler;
            TxtRateDisk.PreviewTextInput -= handler;
            TxtRateNet.PreviewTextInput -= handler;
            TxtRateCpuTemp.PreviewTextInput -= handler;
            TxtRateGpuTemp.PreviewTextInput -= handler;
            TxtRateGpuDedicated.PreviewTextInput -= handler;
            TxtRateGpuShared.PreviewTextInput -= handler;
        }
        _ratePreviewTextHandlers.Clear();

        // Detach the disk / NIC ComboBox SelectionChanged lambdas that
        // PopulateDisks / PopulateNetworkInterfaces tracked. Without this
        // teardown a Reset-driven re-PopulateUi would leave the old lambdas
        // attached -- they keep firing on every future user interaction, and
        // each one closes over _working calling TriggerLiveUpdate.
        foreach (var handler in _diskComboHandlers)
        {
            ComboDisk.SelectionChanged -= handler;
        }
        _diskComboHandlers.Clear();

        foreach (var handler in _nicComboHandlers)
        {
            ComboNetwork.SelectionChanged -= handler;
        }
        _nicComboHandlers.Clear();
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
        // Opacity (formatted as integer 0%..100% via FormatOpacityValue; see helper below).
        SliderOpacity.Value = _working.General.Opacity;
        TxtOpacity.Text = FormatOpacityValue(_working.General.Opacity);
        var opacityHandler = new RoutedPropertyChangedEventHandler<double>((s, e) =>
        {
            TxtOpacity.Text = FormatOpacityValue(SliderOpacity.Value);
            TriggerLiveUpdate();
        });
        SliderOpacity.ValueChanged += opacityHandler;
        _sliderValueHandlers.Add(opacityHandler);

        // Scale (formatted as "1.0×" / "1.5×" / ...; see FormatScaleValue helper below).
        SliderScale.Value = _working.General.Scale;
        TxtScale.Text = FormatScaleValue(_working.General.Scale);
        var scaleHandler = new RoutedPropertyChangedEventHandler<double>((s, e) =>
        {
            TxtScale.Text = FormatScaleValue(SliderScale.Value);
            TriggerLiveUpdate();
        });
        SliderScale.ValueChanged += scaleHandler;
        _sliderValueHandlers.Add(scaleHandler);
    }

    private void PopulateVisibilityCheckboxes()
    {
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
        ChkEnableHardwareMonitor.IsChecked = _working.General.EnableHardwareMonitor;

        var checkHandler = new RoutedEventHandler((s, e) => TriggerLiveUpdate());
        // ChkEnableHardwareMonitor intentionally NOT subscribed here -- toggling it triggers
        // initialization / shutdown of the LibreHardwareMonitorService in MainWindow, which
        // only runs after the dialog commits via BtnOk_Click + ApplySettingsLive. Subscribing
        // it to TriggerLiveUpdate would cause spurious hardware-monitor churn on every ticked
        // live-preview during scroll / hover interactions before the user actually saves.
        foreach (var chk in new[] { ChkCpu, ChkRam, ChkDisk, ChkNet, ChkCpuTemp, ChkGpuTemp, ChkHardwareLoad, ChkGpuDedicated, ChkGpuShared, ChkCombineCpu, ChkTime, ChkTime24H })
        {
            chk.Checked += checkHandler;
            chk.Unchecked += checkHandler;
            _checkboxHandlers.Add(checkHandler);
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
            _rateTextChangedHandlers.Add(textChangedHandler);
            _ratePreviewTextHandlers.Add(previewTextHandler);
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
                    (parent.Children[2] as TextBlock)?.SetText(hex);
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

            SelectionChangedEventHandler diskHandler = (s, e) =>
            {
                if (ComboDisk.SelectedItem is string sel)
                {
                    _working.General.DiskInstanceName = sel;
                    TriggerLiveUpdate();
                }
            };
            ComboDisk.SelectionChanged += diskHandler;
            _diskComboHandlers.Add(diskHandler);
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

            SelectionChangedEventHandler nicHandler = (s, e) =>
            {
                if (ComboNetwork.SelectedItem is string sel)
                {
                    _working.General.NetworkInterfaceName =
                        (sel == "(All Interfaces)") ? null : sel;
                    TriggerLiveUpdate();
                }
            };
            ComboNetwork.SelectionChanged += nicHandler;
            _nicComboHandlers.Add(nicHandler);
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

        // Compat shim: flip WasSaved so MainWindow.OpenSettingsAndNavigateTo's Closed
        // subscriber calls ApplySettings(). The legacy DialogResult setter is intentionally
        // dropped -- it InvalidOperationException-throws on modeless Show()'d windows, and
        // MainWindow creates this dialog as modeless so the user can still drag the bar.
        WasSaved = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _original.General = _snapshotBeforeEdit.General;
            _original.Window = _snapshotBeforeEdit.Window;
            _original.Colors = _snapshotBeforeEdit.Colors;
            _original.Visibility = _snapshotBeforeEdit.Visibility;
            _original.Rates = _snapshotBeforeEdit.Rates;

            if (Owner is MainWindow mw)
                mw.ApplySettingsLive(_original);
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.CancelRevert: {ex}");
        }

        // WasSaved stays at its default false here -- no explicit flip needed (matches the
        // initial property state). DialogResult setter removed (it's been removed on the OK
        // path too) because it InvalidOperationException-throws on modeless Show()'d windows;
        // MainWindow reads WasSaved via its Closed subscriber instead. See BtnOk_Click for
        // the broader avoid-DialogResult reasoning.
        Close();
    }

    /// <summary>
    /// Replaces <c>_working</c> with a default-constructed AppSettings (the same
    /// values AppSettings.Load writes to settings.json on first launch when no
    /// settings file exists), re-populates the dialog UI against the fresh
    /// _working, and live-previews the reset on the bar so the user sees the
    /// defaults land without having to OK the dialog.
    ///
    /// Safe under Cancel: _snapshotBeforeEdit is unchanged, so BtnCancel_Click
    /// still restores the values the dialog opened with -- the Reset is freely
    /// reversible via Cancel. The Reset tabindex (27) puts it after every other
    /// keyboard-focusable control so tabbing through the dialog reaches it
    /// LAST, matching the "I'm done fiddling, want to bail" mental model.
    /// </summary>
    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // _working is readonly, so reset by copying each nested reference
            // from a transient new AppSettings() rather than reassigning.
            // The transient's auto-property initializers supply the factory
            // defaults so we don't hardcode each one (which would silently
            // drift on any future AppSettings field addition).
            var defaults = new AppSettings();
            _working.General       = defaults.General;
            _working.Window        = defaults.Window;
            _working.Colors        = defaults.Colors;
            _working.Visibility    = defaults.Visibility;
            _working.Rates         = defaults.Rates;
            _working.MaxValues     = defaults.MaxValues;
            _working.SectionColors = defaults.SectionColors;
            // `defaults` falls out of scope at method return and is GC'd.

            // Unsubscribe the old per-control handlers before PopulateUi re-adds
            // them so a single Slider drag doesn't fire both old + new lambdas.
            UnsubscribeDialogHandlers();

            // Re-fill every control group against the fresh _working.
            // PopulateColors starts with ColorsPanel.Children.Clear() and
            // PopulateRateTextboxes calls ValidateAll() at the end -- the
            // reset UI lands in a consistent, validated state.
            PopulateUi();

            // Mirror the existing slider-drag live-preview path so MainWindow
            // picks up the defaults immediately. ApplyChangesLive copies
            // _working -> _original and forwards to MainWindow.ApplySettingsLive
            // -- same contract as the slider/checkbox drag ticks.
            ApplyChangesLive();
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.BtnReset_Click: {ex}");
        }
    }

    private void ApplyChangesLive()
    {
        if (!ValidateAll()) return;

        ApplyValuesToWorking();
        CopyWorkingToOriginal();

        if (Owner is MainWindow mw)
            mw.ApplySettingsLive(_original);
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
        _working.General.EnableHardwareMonitor = ChkEnableHardwareMonitor.IsChecked == true;

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
        err?.SetText(message);
        err?.SetValue(VisibilityProperty, Visibility.Visible);
        tb.BorderBrush = System.Windows.Media.Brushes.Red;
        tb.ToolTip = message;
    }

    private void ClearError(WnControls.TextBox tb, TextBlock? err)
    {
        err?.SetText(string.Empty);
        err?.SetValue(VisibilityProperty, Visibility.Collapsed);
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

    /// <summary>
    /// Formats the Opacity slider value (0.0..1.0 stored, 0..100 displayed)
    /// as an integer percent using <see cref="CultureInfo.InvariantCulture"/>
    /// so a comma-decimal locale doesn't double up separators. Math.Round uses
    /// the .NET 6+ default banker's-rounding which is tolerable for percent
    /// display; matches the modernized dialog's badge format.
    /// </summary>
    private static string FormatOpacityValue(double v) =>
        ((int)Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Formats the Scale slider value (0.5..2.0) as "1.0×" / "1.5×" / "2.0×"
    /// using <see cref="CultureInfo.InvariantCulture"/>'s decimal point so
    /// the value stays parseable across locales. The multiplication sign
    /// (U+00D7) reads better than ASCII "x" on HiDPI displays and matches
    /// FormatScaleValue / FormatOpacityValue parity with the modernized
    /// dialog's slider badge format.
    /// </summary>
    private static string FormatScaleValue(double v) =>
        v.ToString("F2", CultureInfo.InvariantCulture) + "\u00d7";
}

// Extension method to avoid type casting
internal static class SettingsWindowExtensions
{
    public static void SetText(this TextBlock tb, string text) => tb.Text = text;
}
