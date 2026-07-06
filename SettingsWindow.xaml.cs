using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WnControls = System.Windows.Controls;
#if !DESIGN_TIME
using WnForms = System.Windows.Forms;
#endif
using WinMeters.Controls;

namespace WinMeters;

/// <summary>
/// Settings dialog rewritten to consume the new MetricCard UserControl. The
/// 5 main meters (CPU / RAM / GPU / Net / Disk) live as 5 instances of
/// MetricCard on the Monitoring page, each holding its Show toggle,
/// Max-value, Refresh-rate, and Section color in one place. Lock-position
/// toggles, sub-meter toggles, theme-token color pickers, and the
/// meter-display-order list keep their individual x:Names -- they're not
/// per-meter controls and don't fit the MetricCard pattern. Every toggle is
/// a plain CheckBox + the hand-built WinMetersToggleSwitch style.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;
    private readonly AppSettings _snapshotBeforeEdit;

    /// <summary>
    /// Static metric-key -> AppSettings wiring helper. Each entry tells the
    /// populate / save loops which sub-object holds the meter state. Adding
    /// a new meter means adding one entry here + one &lt;ctrl:MetricCard&gt;
    /// x:Name to the XAML.
    /// </summary>
    private static readonly MetricBinding[] MetricBindings =
    {
        new("Cpu",  "Cpu",      // MetricKey, Rate-binding key in AppSettings.Rates
            (s, v) => s.Visibility.ShowCpu = v,
            (s) => s.Visibility.ShowCpu,
            (s, v) => s.Rates.Cpu = v,
            (s) => s.Rates.Cpu,
            (s, v) => s.MaxValues.Cpu = v,
            (s) => s.MaxValues.Cpu),
        new("Ram",  "Ram",
            (s, v) => s.Visibility.ShowRam = v,
            (s) => s.Visibility.ShowRam,
            (s, v) => s.Rates.Ram = v,
            (s) => s.Rates.Ram,
            (s, v) => s.MaxValues.Ram = v,
            (s) => s.MaxValues.Ram),
        // Gpu card aggregates ShowGpuDedicated + ShowGpuShared into one
        // toggle. The MetricCard's IsShown boolean reflects "any GPU pie
        // shown"; we OR the two on save.
        new("Gpu",  "GpuDedicated",
            (s, v) => { if (v) { s.Visibility.ShowGpuDedicated = true; s.Visibility.ShowGpuShared = true; } else { s.Visibility.ShowGpuDedicated = false; s.Visibility.ShowGpuShared = false; } },
            (s) => s.Visibility.ShowGpuDedicated && s.Visibility.ShowGpuShared,
            (s, v) => s.Rates.GpuDedicated = v,
            (s) => s.Rates.GpuDedicated,
            (s, v) => s.MaxValues.Gpu = v,
            (s) => s.MaxValues.Gpu),
        new("Net",  "Net",
            (s, v) => s.Visibility.ShowNet = v,
            (s) => s.Visibility.ShowNet,
            (s, v) => s.Rates.Net = v,
            (s) => s.Rates.Net,
            (s, v) => s.MaxValues.Net = v,
            (s) => s.MaxValues.Net),
        new("Disk", "Disk",
            (s, v) => s.Visibility.ShowDisk = v,
            (s) => s.Visibility.ShowDisk,
            (s, v) => s.Rates.Disk = v,
            (s) => s.Rates.Disk,
            (s, v) => s.MaxValues.Disk = v,
            (s) => s.MaxValues.Disk),
    };

    private static readonly Dictionary<string, MetricCard> MetricCardsByKey = new();

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

    // Nav rail collapse/expand animation. Width is animated on
    // LeftRailBorder.Border.WidthProperty directly (Width is a double,
    // so a stock DoubleAnimation works without a custom
    // GridLengthAnimation). The outer column is Width=Auto MinWidth=0
    // and tracks the Border.
    private const double RailWidthExpanded      = 200.0;
    private const double RailWidthCollapsed     = 48.0;
    private const double RailAnimationDurationMs = 150.0;
    private bool _isNavigating;
    // Sticky flag flipped by Card_ValidationFailed. Reset at start of every
    // PopulateUi to clear stale errors from a prior session. SettingsWindow
    // blocks save while this is true -- inline errors on the MetricCard
    // already call out which input is bad.
    private bool _hasValidationError;

    public SettingsWindow(AppSettings original)
    {
        // Assign backing fields BEFORE InitializeComponent so the BAML
        // parser's pre-Connect event wireups (e.g. ComboBox SelectionChanged
        // via inline `SelectionChanged="..."`) find `_working` and
        // `_liveUpdateTimer` already initialised. The Slider ValueChanged
        // handlers are NOT subscribed via XAML attr (intentionally) -- the
        // explicit subscribe happens at the END of PopulateUi() below so
        // the Slider coerce during InitializeComponent can't write through
        // to `_working` and clobber the saved Scale/Opacity.
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

        InitializeComponent();
        PopulateUi();
        SelectSection("Home");

        this.Closing += SettingsWindow_Closing;
        this.ContentRendered += Window_ContentRendered;

        // Set the rail's logical collapsed/expanded state (hamburger
        // IsChecked + text Visibility) but NOT the Width. The actual
        // rail animation runs in Window_ContentRendered after the
        // window is first shown, so the user sees a smooth fade-in
        // animation instead of an instant snap to the persisted state.
        ApplyInitialRailState();

        // Start with the window invisible; Window_ContentRendered will
        // animate it to visible. Combined with the rail animation
        // above, this gives a polished first-show experience.
        Opacity = 0;
    }

    // ---------------------------------------------------------------------
    // Section routing
    // ---------------------------------------------------------------------

    private void NavRail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WnControls.RadioButton rb || rb.Tag is not string tag) return;
        SelectSection(tag);

        // Show click-triggered flyout only when the rail is collapsed
        // (the expanded rail already shows the section name as a label
        // so the flyout would be redundant). NavItemMetadata is the
        // single source of truth for the name + description shown in
        // the flyout -- this also dedupes the descriptions that used
        // to be hardcoded in the XAML rich ToolTip elements (removed
        // in the same commit).
        if (BtnToggleRail.IsChecked == true)
        {
            ShowNavFlyout(rb, tag);
        }
    }

    /// <summary>
    /// Show (or toggle) the nav flyout Popup anchored to the right of
    /// the clicked nav RadioButton. Content is built from
    /// <see cref="NavItemMetadata"/> so the flyout name + description
    /// stay in lock-step with the search filter matches.
    ///
    /// Toggle semantics: clicking the same item that already has the
    /// flyout open closes it (so the user can dismiss without
    /// clicking outside). Clicking a different item switches the
    /// flyout to the new item.
    ///
    /// Flicker avoidance: the <c>IsOpen = true</c> is dispatched via
    /// <see cref="Dispatcher.BeginInvoke(System.Action)"/> so WPF's
    /// <c>StaysOpen="False"</c> close-on-outside-click logic doesn't
    /// immediately close the popup on the very click that opened it
    /// (the click is on the PlacementTarget, which is outside the
    /// Popup's visual area).
    /// </summary>
    private void ShowNavFlyout(WnControls.RadioButton target, string sectionName)
    {
        // Toggle: if the popup is already open for the same item,
        // close it immediately (no deferred dispatch needed -- the
        // popup is already open so there's no flicker to avoid).
        if (NavFlyout.IsOpen && ReferenceEquals(NavFlyout.PlacementTarget, target))
        {
            NavFlyout.IsOpen = false;
            return;
        }

        foreach (var (name, description) in NavItemMetadata)
        {
            if (name == sectionName)
            {
                FlyoutTitle.Text       = name;
                FlyoutDescription.Text = description;
                NavFlyout.PlacementTarget = target;
                // Defer the IsOpen=true to after the click event
                // completes so StaysOpen=False doesn't immediately
                // close it on the same click. Also start the
                // Popup at Opacity=0 and animate to 1 over 150ms
                // (CubicEase EaseInOut) for a smooth fade-in. The
                // close (StaysOpen=False or toggle-close) is a snap
                // -- the fade-in is the polish.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NavFlyout.Opacity = 0;
                    NavFlyout.IsOpen  = true;
                    var fadeIn = new DoubleAnimation
                    {
                        To            = 1,
                        Duration      = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                    };
                    NavFlyout.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }));
                return;
            }
        }
    }

    private void HomeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WnControls.Button btn && btn.Tag is string tag)
        {
            SelectSection(tag);
        }
    }

    /// <summary>
    /// Hamburger toggle in the nav rail header. Drives
    /// AnimateRailWidth between RailWidthExpanded (200px) and
    /// RailWidthCollapsed (48px) so the rail matches the kil0bit
    /// NavigationView's left-pane collapse / expand behaviour. State
    /// is held in the ToggleButton's IsChecked (true = collapsed).
    /// </summary>
    private void BtnToggleRail_Click(object sender, RoutedEventArgs e)
    {
        bool collapse = BtnToggleRail.IsChecked == true;
        AnimateRailWidth(collapse ? RailWidthCollapsed : RailWidthExpanded, collapse);

        // Persist the new state to both _working (so a subsequent
        // live update carries it) and _original (so the next
        // SettingsWindow opening reads the correct value). The
        // standard cancel-via-X path in SettingsWindow_Closing
        // reverts _original to the pre-edit snapshot, which would
        // otherwise undo the rail toggle -- that handler
        // special-cases NavRailCollapsed to preserve it across the
        // revert. We treat the rail state as window UI chrome, not
        // as a user-configurable setting, so it should survive a
        // cancel.
        _working.General.NavRailCollapsed  = collapse;
        _original.General.NavRailCollapsed = collapse;
        _original.Save();
    }

    /// <summary>
    /// Single source of truth for the nav item display name + the
    /// one-line description shown in the nav flyout Popup. If a
    /// 6th nav item lands here, both the XAML nav-item section and
    /// this table need editing.
    /// </summary>
    private static readonly (string Name, string Description)[] NavItemMetadata = new[]
    {
        ("Home",       "Overview and quick links"),
        ("General",    "Lock position, hide in fullscreen, refresh rate"),
        ("Monitoring", "Per-meter show / max-value / refresh-rate / colour"),
        ("Appearance", "Scale, opacity, theme accent, background, border"),
        ("About",      "Version info and project links"),
    };

    /// <summary>
    /// First-show fade-in. Animates the window's Opacity from 0 to 1
    /// over 250ms (CubicEase EaseInOut), and if the persisted rail
    /// state is collapsed, also animates LeftRailBorder.Width from
    /// the XAML default 200px to the collapsed 48px. The user sees a
    /// smooth window-appearing animation with the rail collapsing in
    /// place if needed. Fires only once -- the handler unsubscribes
    /// itself on first invocation.
    /// </summary>
    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= Window_ContentRendered; // only animate once

        var fadeIn = new DoubleAnimation
        {
            From          = 0,
            To            = 1,
            Duration      = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        BeginAnimation(Window.OpacityProperty, fadeIn);

        if (_working.General.NavRailCollapsed)
        {
            var railAnimation = new DoubleAnimation
            {
                To            = RailWidthCollapsed,
                Duration      = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            LeftRailBorder.BeginAnimation(Border.WidthProperty, railAnimation);
        }
    }

    /// <summary>
    /// Snap the rail to its persisted collapsed/expanded state on
    /// first show. Sets the logical state (hamburger IsChecked + text
    /// Visibility) but NOT the Width -- the actual rail animation
    /// runs in Window_ContentRendered after the window is first
    /// shown, so the user sees a smooth fade-in animation instead of
    /// an instant snap to the persisted state. Called once from the
    /// ctor after <c>InitializeComponent</c> so the named elements
    /// exist.
    /// </summary>
    private void ApplyInitialRailState()
    {
        if (!_working.General.NavRailCollapsed) return;

        BtnToggleRail.IsChecked = true;
        SetRailCollapsedState(Visibility.Collapsed);
    }

    /// <summary>
    /// Animates LeftRailBorder's Width with a 150ms cubic ease and
    /// toggles Visibility on the nav text labels in lock-step. On
    /// collapse: text labels are hidden immediately so they cannot
    /// overflow the shrinking 48px rail. On expand: text labels stay
    /// hidden during the animation and are revealed on the Completed
    /// event, so the user sees a clean snap from collapsed to fully
    /// expanded with no peek of a 1-2 character sliver poking out of
    /// the narrow column in the early frames.
    /// </summary>
    private void AnimateRailWidth(double targetWidth, bool collapse)
    {
        if (collapse) SetRailCollapsedState(Visibility.Collapsed);

        var animation = new DoubleAnimation
        {
            To          = targetWidth,
            Duration    = TimeSpan.FromMilliseconds(RailAnimationDurationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        if (!collapse) animation.Completed += ExpandAnimation_Completed;
        LeftRailBorder.BeginAnimation(Border.WidthProperty, animation);
    }

    /// <summary>
    /// Reveal the nav text labels + the WinMeters title once the
    /// expand animation has finished. Guarded on BtnToggleRail.IsChecked
    /// so a rapid collapse mid-expand (which cancels the expand
    /// animation and reuses no events) doesn't accidentally re-show
    /// the labels.
    /// </summary>
    private void ExpandAnimation_Completed(object? sender, EventArgs e)
    {
        if (BtnToggleRail.IsChecked == true) return; // user cancelled expansion
        SetRailCollapsedState(Visibility.Visible);
    }

    /// <summary>
    /// Single source of truth for the 6 visibility targets that toggle
    /// in lock-step with the rail collapse / expand animation: the
    /// WinMeters title and the 5 nav item labels. If a future 6th
    /// nav item lands here, only this method needs editing.
    /// </summary>
    private void SetRailCollapsedState(Visibility visibility)
    {
        RailTitle.Visibility         = visibility;
        NavHomeText.Visibility       = visibility;
        NavGeneralText.Visibility    = visibility;
        NavMonitoringText.Visibility = visibility;
        NavAppearanceText.Visibility = visibility;
        NavAboutText.Visibility      = visibility;
    }

    /// <summary>
    /// Switches the visible section (Home / General / Monitoring / Appearance / About)
    /// and mirrors the selection in the nav rail. Public so MainWindow's RMB-menu
    /// About entry (and any future deep-link entry point) can call
    /// OpenSettingsAndNavigateTo("About") and land on the right tab without
    /// re-creating the dialog. Reentrancy-guarded by <c>_isNavigating</c> so the
    /// nav-rail RadioButton SelectionChanged callbacks can't recurse into this
    /// method while it's mid-flight.
    /// </summary>
    public void SelectSection(string sectionName)
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
        _hasValidationError = false;
        PopulateGeneralToggles();
        PopulateAppearance();
        PopulateDisks();
        PopulateNetworkInterfaces();
        PopulateMeterOrder();
        PopulateAbout();
        PopulateMetrics();
        // Idempotent event attach. The XAML-side `ValueChanged="..."` attr
        // is intentionally omitted (SettingsWindow.xaml's SliderScale and
        // SliderOpacity) so the BAML Connect step does NOT wire those
        // handlers during InitializeComponent -- the Slider coerce firing
        // ValueChanged there would otherwise write through to `_working`
        // and clobber the saved Scale/Opacity. Subscribe-then-set keeps
        // the Reset-all path's second PopulateUi() reseat from double-tap
        // (same rationale as PopulateMetrics' card handlers below; the
        // -= on a never-subscribed event is a safe no-op on a C# event).
        SliderScale.ValueChanged   -= SliderScale_ValueChanged;
        SliderScale.ValueChanged   += SliderScale_ValueChanged;
        SliderOpacity.ValueChanged -= SliderOpacity_ValueChanged;
        SliderOpacity.ValueChanged += SliderOpacity_ValueChanged;
        PopulateNavTooltips();
    }

    private void PopulateGeneralToggles()
    {
        var card = new[]
        {
            ("LockPosition",         _working.Window.LockPosition),
            ("HideInFullscreen",     _working.General.HideInFullscreen),
            ("SnapToTaskbar",        _working.Window.StickToTaskbar),
            ("KeepOnTop",            _working.General.KeepOnTop),
            ("Time24H",              _working.General.Time24H),
            ("CombineLogicalCores",  _working.General.CombineLogicalCores),
        };
        foreach (var (tag, value) in card)
        {
            if (FindToggleByTag(tag) is { } ts) ts.IsChecked = value;
        }

        // Sub-meter toggles (CpuTemp / GpuTemp / HardwareLoad / Time)
        var subs = new (string tag, bool value)[]
        {
            ("ShowCpuTemp",       _working.Visibility.ShowCpuTemp),
            ("ShowGpuTemp",       _working.Visibility.ShowGpuTemp),
            ("ShowHardwareLoad",  _working.Visibility.ShowHardwareLoad),
            ("ShowTime",          _working.Visibility.ShowTime),
        };
        foreach (var (tag, value) in subs)
        {
            if (FindToggleByTag(tag) is { } ts) ts.IsChecked = value;
        }

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
    }

    /// <summary>
    /// Walks the visual tree to find a ui:ToggleSwitch with the given Tag.
    /// Toggles on the General / Sub-Meter pages are tagged by their
    /// canonical short key (LockPosition, ShowCpuTemp, etc.); we scan
    /// descendants of the SettingsWindow so we don't have to enumerate
    /// each card positionally.
    /// </summary>
    private WnControls.CheckBox? FindToggleByTag(string tag)
    {
        return FindVisualChildren<WnControls.CheckBox>(this)
            .FirstOrDefault(t => (t.Tag as string) == tag);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var grand in FindVisualChildren<T>(child)) yield return grand;
        }
    }

    private void PopulateAppearance()
    {
        SliderScale.Value   = _working.General.Scale;
        SliderOpacity.Value = _working.General.Opacity;

        SetSwatch(SwatchAccent,     _working.Colors.Accent);
        SetSwatch(SwatchBackground, _working.Colors.Background);
        SetSwatch(SwatchBorder,     _working.Colors.Border);
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
            // Compose the path from two nullable sources: Environment.ProcessPath
            // is nullable on its own, and Process.GetCurrentProcess().MainModule
            // is nullable too, with .FileName a third nullable layer. The
            // resulting expression is therefore string?, but the downstream
            // `string.IsNullOrEmpty(assemblyPath) && File.Exists(assemblyPath)`
            // check already short-circuits to skip work on null, so we just
            // type the local as nullable instead of suppressing the warning
            // with `!` (which would mislead future readers).
            string? assemblyPath = Environment.ProcessPath
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

    /// <summary>
    /// Bind the 5 MetricCard instances to their corresponding settings
    /// rows (IsShown, MaxValue, RefreshRate, SectionColor). One card per
    /// binding entry above; the dictionary keeps x:Name lookups out of
    /// the loop so adding a new meter stays one line.
    /// </summary>
    private void PopulateMetrics()
    {
        MetricCardsByKey.Clear();
        MetricCardsByKey["Cpu"]  = CardCpu;
        MetricCardsByKey["Ram"]  = CardRam;
        MetricCardsByKey["Gpu"]  = CardGpu;
        MetricCardsByKey["Net"]  = CardNet;
        MetricCardsByKey["Disk"] = CardDisk;

        foreach (var binding in MetricBindings)
        {
            if (!MetricCardsByKey.TryGetValue(binding.MetricKey, out var card)) continue;
            card.IsShown         = binding.ReadIsShown(_working);
            card.MaxValueText    = binding.ReadMaxValue(_working).ToString(System.Globalization.CultureInfo.InvariantCulture);
            card.RefreshRateText = (binding.ReadRefreshRate(_working) ?? _working.General.RefreshRateMs).ToString();

            if (_working.SectionColors.TryGetValue(binding.MetricKey, out var hex))
                card.SectionColorHex = hex;

            // Idempotent event attach. unsubscribe-then-subscribe - never
            // double-tap on subsequent PopulateUi calls (BIND-77x redo).
            card.IsShownChanged       -= Card_IsShownChanged;
            card.MaxValueChanged      -= Card_MaxValueChanged;
            card.RefreshRateChanged   -= Card_RefreshRateChanged;
            card.SectionColorChanged  -= Card_SectionColorChanged;
            card.ValidationFailed     -= Card_ValidationFailed;

            card.IsShownChanged       += Card_IsShownChanged;
            card.MaxValueChanged      += Card_MaxValueChanged;
            card.RefreshRateChanged   += Card_RefreshRateChanged;
            card.SectionColorChanged  += Card_SectionColorChanged;
            card.ValidationFailed     += Card_ValidationFailed;
        }
    }

    private static void SetSwatch(Border swatch, string hex)
    {
        swatch.Background = ColorHelper.ParseBrush(hex);
    }

    /// <summary>
    /// Build rich hover ToolTips on the 5 nav RadioButtons from
    /// <see cref="NavItemMetadata"/>. The ToolTip uses the global
    /// WinMetersTooltip style (dark card surface, drop shadow) -- we
    /// just provide the content (bold section name + one-line
    /// description). Same source of truth as the click-flyout Popup,
    /// so the description text stays in lock-step with the flyout
    /// description.
    /// </summary>
    private void PopulateNavTooltips()
    {
        var radios = new[] { NavHome, NavGeneral, NavMonitoring, NavAppearance, NavAbout };
        foreach (var rb in radios)
        {
            if (rb.Tag is not string tag) continue;
            var (name, description) = NavItemMetadata.FirstOrDefault(
                x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(name)) continue;

            rb.ToolTip = new WnControls.ToolTip
            {
                Content = new WnControls.StackPanel
                {
                    Children =
                    {
                        new WnControls.TextBlock
                        {
                            Text       = name,
                            FontWeight = FontWeights.SemiBold,
                            FontSize   = 13,
                        },
                        new WnControls.TextBlock
                        {
                            Text         = description,
                            Opacity      = 0.7,
                            FontSize     = 11,
                            MaxWidth     = 200,
                            TextWrapping = TextWrapping.Wrap,
                            Margin       = new Thickness(0, 2, 0, 0),
                        },
                    },
                },
            };
        }
    }

    // ---------------------------------------------------------------------
    // Generic toggle / slider / combobox handlers
    // ---------------------------------------------------------------------

    private void GenericToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WnControls.CheckBox ts || ts.Tag is not string tag) return;
        bool value = ts.IsChecked == true;

        switch (tag)
        {
            case "LockPosition":         _working.Window.LockPosition = value; break;
            case "SnapToTaskbar":        _working.Window.StickToTaskbar = value; break;
            case "HideInFullscreen":     _working.General.HideInFullscreen = value; break;
            case "KeepOnTop":            _working.General.KeepOnTop = value; break;
            case "Time24H":              _working.General.Time24H = value; break;
            case "CombineLogicalCores":  _working.General.CombineLogicalCores = value; break;
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

    // ---------------------------------------------------------------------
    // MetricCard event handlers - one per DP, with binding lookup
    // ---------------------------------------------------------------------

    private void Card_IsShownChanged(object? sender, EventArgs e)
    {
        if (sender is not MetricCard card) return;
        var binding = FindBinding(card.MetricKey);
        if (binding is null) return;
        binding.WriteIsShown(_working, card.IsShown);
        TriggerLiveUpdate();
    }

    private void Card_MaxValueChanged(object? sender, MaxValueChangedEventArgs e)
    {
        var binding = FindBinding(e.MetricKey);
        if (binding is null) return;
        binding.WriteMaxValue(_working, e.Value);
        TriggerLiveUpdate();
    }

    private void Card_RefreshRateChanged(object? sender, RefreshRateChangedEventArgs e)
    {
        var binding = FindBinding(e.MetricKey);
        if (binding is null) return;
        binding.WriteRefreshRate(_working, e.Value);
        TriggerLiveUpdate();
    }

    private void Card_SectionColorChanged(object? sender, SectionColorChangedEventArgs e)
    {
        if (sender is not MetricCard card) return;
        _working.SectionColors[card.MetricKey] = e.Hex;
        TriggerLiveUpdate();
    }

    private void Card_ValidationFailed(object? sender, ValidationFailedEventArgs e)
    {
        // MetricCard already shows inline errors next to the offending textbox.
        // We mirror that into a sticky flag so the Save button refuses to
        // commit until the user fixes the value (clicked twice + blob
        // submitted by Enter triggers Unsubscribe-on-empty-value too).
        _hasValidationError = true;
        TriggerLiveUpdate();
    }

    private static MetricBinding? FindBinding(string metricKey) =>
        MetricBindings.FirstOrDefault(b => b.MetricKey == metricKey);

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

    /// <summary>
    /// Single colour-picker used by the 3 theme-token (Accent / Background /
    /// Border) rows on the Appearance page. MetricCard handles its own
    /// SectionColours internally; the legacy 14 per-meter colour rows are
    /// gone -- SectionColors takes their place.
    /// </summary>
    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WnControls.Button btn || btn.Tag is not string tag) return;
        string currentHex = tag switch
        {
            "Accent"     => _working.Colors.Accent,
            "Background" => _working.Colors.Background,
            "Border"     => _working.Colors.Border,
            _            => "#FFFFFF"
        };
        Border? swatch = tag switch
        {
            "Accent"     => SwatchAccent,
            "Background" => SwatchBackground,
            "Border"     => SwatchBorder,
            _            => null
        };

#if !DESIGN_TIME
        try
        {
            using var dlg = new WnForms.ColorDialog
            {
                FullOpen = true,
                Color = ColorHelper.ToDrawingColor(currentHex)
            };
            if (dlg.ShowDialog() != WnForms.DialogResult.OK) return;

            string alpha = "FF";
            if (currentHex.Length == 9) alpha = currentHex.Substring(1, 2);
            else if (tag == "Background") alpha = "B4";

            string hex = $"#{alpha}{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";

            switch (tag)
            {
                case "Accent":     _working.Colors.Accent = hex; break;
                case "Background": _working.Colors.Background = hex; break;
                case "Border":     _working.Colors.Border = hex; break;
            }
            if (swatch is not null) SetSwatch(swatch, hex);
            TriggerLiveUpdate();
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow.ColorButton_Click: {ex}");
        }
#endif
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
        // Block save while any MetricCard has an unresolved validation
        // error. The corresponding inline error messages are already
        // visible next to the offending input; jumping to Monitoring so
        // the user lands on the broken card.
        if (_hasValidationError)
        {
            System.Windows.MessageBox.Show(this,
                "One or more per-meter values are invalid. Fix the highlighted inputs before saving.",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SelectSection("Monitoring");
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
        _working.General      = defaults.General;
        _working.Window       = defaults.Window;
        _working.Colors       = defaults.Colors;
        _working.Visibility   = defaults.Visibility;
        _working.Rates        = defaults.Rates;
        _working.MaxValues    = defaults.MaxValues;
        _working.SectionColors = defaults.SectionColors;

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
        if (DialogResult == true) return;

        // Preserve the nav rail collapse state across the cancel
        // revert. The standard snapshot-restore below would clobber
        // it because the snapshot predates the toggle -- but the
        // rail state is window UI chrome, not a user-configurable
        // setting, so a close-via-X should NOT undo it.
        bool savedRailState = _original.General.NavRailCollapsed;

        try
        {
            var restored = JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(_snapshotBeforeEdit)) ?? new AppSettings();
            _original.General      = restored.General;
            _original.Window       = restored.Window;
            _original.Colors       = restored.Colors;
            _original.Visibility   = restored.Visibility;
            _original.Rates        = restored.Rates;
            _original.MaxValues    = restored.MaxValues;
            _original.SectionColors = restored.SectionColors;
            if (Owner is MainWindow mw) mw.ApplySettingsLive(_original);
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"SettingsWindow cancel-revert: {ex}");
        }

        _original.General.NavRailCollapsed = savedRailState;
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
        _original.General      = _working.General;
        _original.Window       = _working.Window;
        _original.Colors       = _working.Colors;
        _original.Visibility   = _working.Visibility;
        _original.Rates        = _working.Rates;
        _original.MaxValues    = _working.MaxValues;
        _original.SectionColors = _working.SectionColors;
    }

    private void TriggerLiveUpdate()
    {
        _liveUpdateTimer.Stop();
        _liveUpdateTimer.Start();
    }

    private void ApplyChangesLive()
    {
        if (!IsLoaded) return;
        CopyWorkingToOriginal();
        if (Owner is MainWindow mw) mw.ApplySettingsLive(_original);
    }

    private class MeterOrderItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-meter wiring record. Holds lambdas that read/write the four
    /// settings sub-objects (Visibility.X / Rates.X / MaxValues.X /
    /// MetricCard.SectionColorHex). Keeps PopulateMetrics / card-event
    /// handlers one-liners. Adding a new meter = one MetricBinding entry
    /// + one &lt;ctrl:MetricCard&gt; XAML element.
    /// </summary>
    private sealed record MetricBinding(
        string MetricKey,
        string RateKey,
        Action<AppSettings, bool>  WriteIsShown,
        Func<AppSettings, bool>    ReadIsShown,
        Action<AppSettings, int?>  WriteRefreshRate,
        Func<AppSettings, int?>    ReadRefreshRate,
        Action<AppSettings, double> WriteMaxValue,
        Func<AppSettings, double>  ReadMaxValue);
}
