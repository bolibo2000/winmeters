// IMPORTANT — CHECKBOX ALIAS DISCIPLINE:
//
// This file keeps TWO usings for System.Windows.Controls:
//
//   using System.Windows.Controls;       // exposes TextBlock, StackPanel, Grid, Border, … directly
//   using WnControls = System.Windows.Controls;  // expose via alias for collision-prone references
//
// The plain using is what enables the bare `TextBlock` / `StackPanel` / `Grid`
// references throughout this file (otherwise we'd need `WnControls.TextBlock`
// everywhere, ~120 lines of noise). It is ALSO what triggered the CS0104
// "ambiguous reference" when an unqualified `CheckBox` collided with the
// `WnForms.CheckBox` brought in by the conditional `using
// WnForms = System.Windows.Forms;` at design-time.
//
// Therefore: any WPF control type that has a same-name WinForms counterpart
// (CheckBox today; ComboBox, Button, TextBox for symmetry) MUST be referenced
// as `WnControls.CheckBox` etc. — never unqualified. The qualifying `WnControls.`
// alias wins over the plain using, so the design-time ambiguity never resurfaces.
// The WnControls qualifier explicitly annotates every collision-prone reference
// in this file (PopulateHotkey's TxtHotkey/LblHotkeyStatus, PopulateVisibilityCheckboxes,
// ApplyValuesToWorking's checkbox tuple list, etc.).
//
// Do NOT drop the plain `using System.Windows.Controls;` line. If you do, every
// bare `TextBlock` / `StackPanel` / `Grid` / `Border` / `WrapPanel` / `Orientation`
// reference will fail to compile (CS0246). Either keep both usings together and
// discipline the CheckBox alias as above, or migrate every WPF reference in this
// file to the WnControls qualifier — that is a much larger sweep and not worth
// the churn.

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
#endif
using WinMeters.Services;
// NOTE: SettingsBindings (the string-driven checkbox \(\rightarrow\) AppSettings bindings) is
// in Services/SettingsBindings.cs to keep this file's WPF-coupled code-behind separate
// from the testable apply-side bindings. The test project compiles it via <Compile Include>
// so Tests/SettingsBindingsTests can pin ChkKeepOnTop \(\rightarrow\) General.KeepOnTop.

namespace WinMeters;

public partial class SettingsWindow : Window
{
    public bool WasSaved { get; private set; }

    private readonly AppSettings _original;
    private readonly AppSettings _working;
    private readonly AppSettings _snapshotBeforeEdit;

    private static readonly Dictionary<string, string> FriendlyNames = new()
    {
        ["Cpu"] = "CPU Cores Usage",
        ["Ram"] = "Total RAM Usage",
        ["Disk"] = "Disk Activity",
        ["Net"] = "Network Activity",
        ["H/W Temps"] = "H/W Temperatures",
        ["GpuDedicated"] = "GPU VRAM Usage",
        ["GpuShared"] = "GPU SRAM Usage",
        ["Time"] = "System Time"
    };

    private readonly List<RoutedPropertyChangedEventHandler<double>> _sliderValueHandlers = new();
    private readonly List<RoutedEventHandler> _checkboxHandlers = new();
    private readonly List<TextChangedEventHandler> _rateTextChangedHandlers = new();
    private readonly List<TextCompositionEventHandler> _ratePreviewTextHandlers = new();
    private readonly List<SelectionChangedEventHandler> _diskComboHandlers = new();
    private readonly List<SelectionChangedEventHandler> _nicComboHandlers  = new();

    private System.Windows.Threading.DispatcherTimer? _liveUpdateTimer;
    private const int LiveUpdateDebounceMs = 100;

    // Stored as fields so TxtHotkey_TextChanged can unsubscribe in UnsubscribeDialogHandlers
    // without a stale capture of the closure tearing down setting reload mid-edit. The handler
    // is wired in PopulateUi; only the TextChanged channel needs an explicit list because WPF's
    // default TextBox subscription model holds the handler alive past Window closure unless
    // we explicitly -= it.
    private TextChangedEventHandler? _hotkeyTextChangedHandler;

    public SettingsWindow(AppSettings original)
    {
        InitializeComponent();

        var menuBackground = ColorHelper.ThemeBrush("ThemeBgBrush");
        var menuForeground = ColorHelper.ThemeBrush("ThemeTextBrush") ?? System.Windows.Media.Brushes.White;
        this.Background = menuBackground;
        RootGrid.Background = menuBackground;
        this.Foreground = menuForeground;

        _original = original ?? throw new ArgumentNullException(nameof(original));
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
        _liveUpdateTimer?.Stop();
        UnsubscribeDialogHandlers();
    }

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
            foreach (var chk in new[] { ChkCpu, ChkRam, ChkDisk, ChkNet, ChkCpuTemp, ChkGpuTemp, ChkGpuDedicated, ChkGpuShared, ChkCombineCpu, ChkTime, ChkTime24H, ChkLockPosition, ChkHideInFullscreen, ChkSnapToTaskbar, ChkKeepOnTop })
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

        foreach (var handler in _diskComboHandlers)
            ComboDisk.SelectionChanged -= handler;
        _diskComboHandlers.Clear();

        foreach (var handler in _nicComboHandlers)
            ComboNetwork.SelectionChanged -= handler;
        _nicComboHandlers.Clear();

        // Detach the hotkey TextChanged handler last (after the per-list
        // tracker cleanup) since it isn't tracked in any list — it's a
        // single delegate stashed in a field. Null-checks keeps the
        // unsubscribe idempotent against SettingsWindow_Closed firing
        // twice in some shutdown paths.
        if (_hotkeyTextChangedHandler is not null && TxtHotkey is not null)
            TxtHotkey.TextChanged -= _hotkeyTextChangedHandler;
        _hotkeyTextChangedHandler = null;
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
        PopulateHotkey();
    }

    private void PopulateHotkey()
    {
        // Designer builds (DesignTimeBuild=true) compile WinMeters.csproj's stripped source
        // set with the SettingsWindow XAML excluded; in that path TxtHotkey + LblHotkeyStatus
        // are never wired and the controls stay null. The Debug.Assert trips immediately so
        // a future contributor restoring the XAML surface at design-time catches the regression
        // at first run instead of seeing a StatusLabel that never updates without explanation.
        // The null-check + return is enough for Release; the assert is purely diagnostic.
        System.Diagnostics.Debug.Assert(
            TxtHotkey is not null && LblHotkeyStatus is not null,
            "TxtHotkey/LblHotkeyStatus missing — XAML surface not wired at design-time?");
        if (TxtHotkey is null || LblHotkeyStatus is null) return;

        TxtHotkey.Text = _working.General.Hotkey;
        UpdateHotkeyStatus();

        // Wire TextChanged once per dialog lifetime. The ??= stores the delegate so we can
        // -= it cleanly in UnsubscribeDialogHandlers without re-finding a closure identity.
        if (_hotkeyTextChangedHandler is null)
        {
            _hotkeyTextChangedHandler = new TextChangedEventHandler((s, e) =>
            {
                UpdateHotkeyStatus();
                TriggerLiveUpdate();
            });
            TxtHotkey.TextChanged += _hotkeyTextChangedHandler;
        }
    }

    private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        // Designer-build guard mirrors PopulateHotkey's null-check pattern — when the
        // XAML surface isn't wired at design-time the button isn't reachable, but we
        // null-check anyway so a future XAML refactor can't silently remove the
        // chord from a designer session. The textbox write fires the cached TextChanged
        // handler (so the status label updates without a second manual call) and
        // TriggerLiveUpdate propagates the new chord to the live preview immediately.
        if (TxtHotkey is null) return;
        TxtHotkey.Text = HotkeyService.DefaultHotkeyString;
        TriggerLiveUpdate();
    }

    private void UpdateHotkeyStatus()
    {
        if (TxtHotkey is null || LblHotkeyStatus is null) return;

        // logWarnings:false so per-keystroke edits don't spam the rolling error log when the
        // user types then deletes (every intermediate chord would otherwise fire a "fallback
        // used" log). Resolve silently here; the in-line label reflects what was registered.
        //
        // The status label is a TextBlock (not a Label) so .Text accepts arbitrary
        // characters (underscore, ampersand) without them being swallowed as WPF
        // access-key accelerators (Label.Content would). This matters when we render
        // the user's raw input back in the warning label — e.g. "Ctrl+Junk" or
        // "Ctrl+++Foo" must display verbatim, not as a hotkey cue.
        string text = TxtHotkey.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            // Empty input falls back to the canonical default chord — call this out so a
            // user who clears the box to "blank out" the config understands what's actually
            // registered. We interpolate HotkeyService.DefaultHotkeyString rather than
            // hard-coding "Ctrl+Alt+Shift+M" so a future shift in the canonical fallback
            // (e.g. to avoid a colliding Win10 system chord) doesn't silently drift from
            // what this label claims.
            LblHotkeyStatus.Text = $"(empty → using default {HotkeyService.DefaultHotkeyString})";
            // Reset foreground explicitly so a previous failed-edit pass doesn't trap
            // the label in the warning color after the user clears the box. WPF's
            // DynamicResource binding (which XAML uses for Foreground=ThemeTextBrush)
            // does NOT auto-restore when we change .Foreground in code, so each state
            // has to set it explicitly.
            LblHotkeyStatus.Foreground = System.Windows.Media.Brushes.LightGray;
            LblHotkeyStatus.ToolTip = null;
            return;
        }

        if (HotkeyService.TryParseHotkeyString(text, out var chord, logWarnings: false))
        {
            // Clean parse — render the resolved chord. FormatChord routes printable VKs
            // through their ASCII character (so the user sees the same letters they
            // typed) and named keys through FriendlyKeyNames ("Ctrl+Shift+F12" not
            // "Ctrl+Shift+Vk0x7B"). User can copy-paste this back into the JSON Hotkey
            // setting without round-tripping through hex math.
            LblHotkeyStatus.Text = $"active: \"{HotkeyService.FormatChord(chord.fsModifiers, chord.vk)}\"";
            // Explicit reset to default color in case the previous edit was caught by
            // the warning branch below.
            LblHotkeyStatus.Foreground = System.Windows.Media.Brushes.LightGray;
            LblHotkeyStatus.ToolTip = null;
        }
        else
        {
            // Parse fell through to the canonical fallback even though the user typed
            // something. Use the ThreeState disclosure: show the raw input verbatim so
            // the user can spot their typo, then the canonical fallback the runtime
            // actually registered so they don't silently end up on Ctrl+Alt+Shift+M
            // for a chord they intended differently. Foreground = Red (same SSKP color
            // used by ShowError's red border on rate Validation) so the cue matches the
            // dialog's existing error-affordance vocabulary.
            //
            // Tooltip carries the verbose explanation: list of supported key tokens.
            // Without it, a user with no parser familiarity lands on a warning label
            // and doesn't know how to fix the chord. With it, they see exactly what's
            // accepted and can repair the input without reading the rolling debug log.
            LblHotkeyStatus.Text = $"\u26a0 '{text}' \u2192 {HotkeyService.DefaultHotkeyString} (parse failed)";
            LblHotkeyStatus.Foreground = System.Windows.Media.Brushes.Red;
            LblHotkeyStatus.ToolTip =
                $"WinMeters couldn't parse '{text}' as a valid hotkey chord. " +
                $"Falling back to {HotkeyService.DefaultHotkeyString}. " +
                "Supported key tokens: single characters (e.g. Ctrl+M), F1\u2013F12, Space, Tab, " +
                "Enter, Esc, Backspace, Up/Down/Left/Right, PageUp/PageDown, Home/End, " +
                "Insert/Delete. Modifier tokens: Ctrl, Alt, Shift, Win (any order).";
        }
    }

    private void PopulateSliders()
    {
        SliderOpacity.Value = _working.General.Opacity;
        TxtOpacity.Text = FormatOpacityValue(_working.General.Opacity);
        var opacityHandler = new RoutedPropertyChangedEventHandler<double>((s, e) =>
        {
            TxtOpacity.Text = FormatOpacityValue(SliderOpacity.Value);
            TriggerLiveUpdate();
        });
        SliderOpacity.ValueChanged += opacityHandler;
        _sliderValueHandlers.Add(opacityHandler);

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
        ChkGpuDedicated.IsChecked = _working.Visibility.ShowGpuDedicated;
        ChkGpuShared.IsChecked = _working.Visibility.ShowGpuShared;
        ChkCombineCpu.IsChecked = _working.General.CombineLogicalCores;
        ChkTime.IsChecked = _working.Visibility.ShowTime;
        ChkTime24H.IsChecked = _working.General.Time24H;
        ChkLockPosition.IsChecked      = _working.Window.LockPosition;
        ChkHideInFullscreen.IsChecked  = _working.General.HideInFullscreen;
        ChkSnapToTaskbar.IsChecked     = _working.Window.StickToTaskbar;
        ChkKeepOnTop.IsChecked         = _working.General.KeepOnTop;

        var checkHandler = new RoutedEventHandler((s, e) => TriggerLiveUpdate());
        foreach (var chk in new[] { ChkCpu, ChkRam, ChkDisk, ChkNet, ChkCpuTemp, ChkGpuTemp, ChkGpuDedicated, ChkGpuShared, ChkCombineCpu, ChkTime, ChkTime24H, ChkLockPosition, ChkHideInFullscreen, ChkSnapToTaskbar, ChkKeepOnTop })
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

        var textChangedHandler = new TextChangedEventHandler((s, e) =>
        {
            if (s is WnControls.TextBox tb)
            {
                var errorBlock = tb.Parent is WnControls.StackPanel panel && panel.Children.Count >= 3
                    ? panel.Children[2] as TextBlock : null;
                if (ValidateRate(tb, errorBlock)) TriggerLiveUpdate();
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

    private void SetupRateError(WnControls.TextBox tb, TextBlock err) => tb.Tag = err;

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
            AddColorEditor(name, setter);
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
            Text = name, Width = 60, VerticalAlignment = VerticalAlignment.Center, FontSize = 10
        });

        var rect = new WnShapes.Rectangle
        {
            Width = 20, Height = 20,
            Stroke = System.Windows.Media.Brushes.Black, StrokeThickness = 1,
            Margin = new Thickness(6, 0, 6, 0),
            Fill = ColorHelper.ParseBrush(GetHex()),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        rect.MouseLeftButtonUp += (s, e) => OpenColorPicker(rect, setter, GetHex);
        panel.Children.Add(rect);

        var txt = new TextBlock
        {
            Text = GetHex(), VerticalAlignment = VerticalAlignment.Center, FontSize = 10, MinWidth = 70
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
                if (rect.Parent is StackPanel parent && parent.Children.Count >= 3)
                    (parent.Children[2] as TextBlock)?.SetText(hex);
                TriggerLiveUpdate();
            }
#else
            rect.Fill = ColorHelper.ParseBrush(getCurrentHex());
#endif
        }
        catch (Exception ex) { WinMeters.Log.D($"SettingsWindow.OpenColorPicker: {ex}"); }
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
        catch (Exception ex) { WinMeters.Log.D($"PopulateDisks: {ex}"); }
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
                ? "(All Interfaces)" : _working.General.NetworkInterfaceName;
            SelectComboItem(ComboNetwork, selectedNet);

            SelectionChangedEventHandler nicHandler = (s, e) =>
            {
                if (ComboNetwork.SelectedItem is string sel)
                {
                    _working.General.NetworkInterfaceName = (sel == "(All Interfaces)") ? null : sel;
                    TriggerLiveUpdate();
                }
            };
            ComboNetwork.SelectionChanged += nicHandler;
            _nicComboHandlers.Add(nicHandler);
        }
        catch (Exception ex) { WinMeters.Log.D($"PopulateNetworkInterfaces: {ex}"); }
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
            System.Windows.MessageBox.Show(this,
                $"One or more refresh rates are invalid. Fix the highlighted values (minimum {Constants.Timing.MinValidationRateMs} ms) before saving.",
                "Validation Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        ApplyValuesToWorking();
        CopyWorkingToOriginal();
        _original.Save();
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
            if (Owner is MainWindow mw) mw.ApplySettingsLive(_original);
        }
        catch (Exception ex) { WinMeters.Log.D($"SettingsWindow.CancelRevert: {ex}"); }
        Close();
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaults = new AppSettings();
            _working.General       = defaults.General;
            _working.Window        = defaults.Window;
            _working.Colors        = defaults.Colors;
            _working.Visibility    = defaults.Visibility;
            _working.Rates         = defaults.Rates;

            UnsubscribeDialogHandlers();
            PopulateUi();
            ApplyChangesLive();
        }
        catch (Exception ex) { WinMeters.Log.D($"SettingsWindow.BtnReset_Click: {ex}"); }
    }

    private void ApplyChangesLive()
    {
        if (!ValidateAll()) return;
        ApplyValuesToWorking();
        CopyWorkingToOriginal();
        if (Owner is MainWindow mw) mw.ApplySettingsLive(_original);
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
        _working.Rates.GpuShared = ParseNullableInt(TxtRateGpuShared.Text);        // Data-driven checkbox → property push. The bindings are owned by
        // Services/SettingsBindings (single source of truth, unit-tested via
        // Tests/SettingsBindingsTests) — the dictionary below resolves each
        // binding's string name to the actual WPF CheckBox instance, which is
        // the only WPF-specific part of the apply path. Adding a new checkbox
        // + property pair is one row in SettingsBindings + one dictionary
        // entry here, not a hand-written assignment that could drift between
        // writers. The wiring-contract test in SettingsBindingsTests pins each
        // row's lambda to the specific AppSettings field it should mutate, so
        // a typo in either side (the binding's lambda or the dictionary lookup
        // here) fails the build at the affected row rather than landing
        // silently in production.
        var chkByName = new Dictionary<string, WnControls.CheckBox>(StringComparer.Ordinal)
        {
            ["ChkCpu"]              = ChkCpu,
            ["ChkRam"]              = ChkRam,
            ["ChkDisk"]             = ChkDisk,
            ["ChkNet"]              = ChkNet,
            ["ChkCpuTemp"]          = ChkCpuTemp,
            ["ChkGpuTemp"]          = ChkGpuTemp,
            ["ChkGpuDedicated"]     = ChkGpuDedicated,
            ["ChkGpuShared"]        = ChkGpuShared,
            ["ChkCombineCpu"]       = ChkCombineCpu,
            ["ChkTime"]             = ChkTime,
            ["ChkTime24H"]          = ChkTime24H,
            ["ChkLockPosition"]     = ChkLockPosition,
            ["ChkHideInFullscreen"] = ChkHideInFullscreen,
            ["ChkSnapToTaskbar"]    = ChkSnapToTaskbar,
            ["ChkKeepOnTop"]        = ChkKeepOnTop,
        };
        // Drift-prevention: every SettingsBindings binding name must have a
        // matching entry in chkByName. Adding a row to SettingsBindings
        // without updating this dictionary surfaces immediately at
        // design-time (Debug build) rather than at runtime with a cryptic
        // KeyNotFoundException. The diagnostic message reports the count
        // mismatch so a contributor can fix both sides in one edit.
        var keys = new HashSet<string>(chkByName.Keys, StringComparer.Ordinal);
        System.Diagnostics.Debug.Assert(
            keys.SetEquals(SettingsBindings.AllBindingNames),
            $"chkByName keys ({keys.Count}) don't SetEquals SettingsBindings.AllBindingNames " +
            $"({SettingsBindings.AllBindingNames.Count}). Either add the missing CheckBox " +
            $"references to chkByName in SettingsWindow.xaml.cs, or update " +
            $"Services/SettingsBindings.cs to match the canonical CheckBox x:Name set.");

        foreach (var (name, apply) in SettingsBindings.GetVisibilityBindings(_working))
            apply(chkByName[name].IsChecked == true);

        // Hotkey text. Trim and default to the canonical default chord when the user clears
        // the box (preserved through the BtnOk path). The fallback reads from
        // HotkeyService.DefaultHotkeyString so a future canonical shift propagates here
        // without further edits.
        _working.General.Hotkey = string.IsNullOrWhiteSpace(TxtHotkey.Text)
            ? HotkeyService.DefaultHotkeyString
            : TxtHotkey.Text.Trim();

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
                    newOrder.Add(item.Key);
            }
            _working.General.MeterOrder = newOrder;
        }
    }

    private static int? ParseNullableInt(string? s) =>
        string.IsNullOrEmpty(s) ? null : (int.TryParse(s, out var v) ? v : null);

    private bool ValidateRate(WnControls.TextBox tb, TextBlock? err)
    {
        var s = tb.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(s)) { ClearError(tb, err); return true; }
        if (!int.TryParse(s, out var v)) { ShowError(tb, err, "Invalid number"); return false; }
        if (v < Constants.Timing.MinValidationRateMs) { ShowError(tb, err, $"Minimum {Constants.Timing.MinValidationRateMs} ms"); return false; }
        ClearError(tb, err);
        return true;
    }

    private bool ValidateAll() =>
        ValidateRate(TxtRateCpu, ErrRateCpu) &&
        ValidateRate(TxtRateRam, ErrRateRam) &&
        ValidateRate(TxtRateDisk, ErrRateDisk) &&
        ValidateRate(TxtRateNet, ErrRateNet) &&
        ValidateRate(TxtRateCpuTemp, ErrRateCpuTemp) &&
        ValidateRate(TxtRateGpuTemp, ErrRateGpuTemp) &&
        ValidateRate(TxtRateGpuDedicated, ErrRateGpuDedicated) &&
        ValidateRate(TxtRateGpuShared, ErrRateGpuShared);

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

    private class MeterOrderItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private static string FormatOpacityValue(double v) =>
        ((int)Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    private static string FormatScaleValue(double v) =>
        v.ToString("F2", CultureInfo.InvariantCulture) + "\u00d7";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = 1;
            int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            if (hr != 0) WinMeters.Log.D($"SettingsWindow.OnSourceInitialized: DWM dark mode HRESULT 0x{hr:X8}.");
        }
        catch (Exception ex) { WinMeters.Log.D($"SettingsWindow.OnSourceInitialized: {ex.Message}"); }
    }
}

internal static class SettingsWindowExtensions
{
    public static void SetText(this TextBlock tb, string text) => tb.Text = text;
}
