using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WnControls = System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinMeters;

/// <summary>
/// Settings dialog rewritten to consume the new MetricCard UserControl. The
/// 5 main meters (CPU / RAM / GPU / Net / Disk) live as 5 instances of
/// MetricCard on the Monitoring page, each holding its Show toggle,
/// Max-value, Refresh-rate, and Section color in one place. Lock-position
/// toggles, sub-meter toggles, theme-token color pickers, and the
/// meter-display-order list keep their individual x:Names --- they're not
/// per-meter controls and don't fit the MetricCard pattern. Every toggle is
/// a plain CheckBox + the hand-built WinMetersToggleSwitch style.
///
/// Partial-class split: the per-section populators / event handlers now
/// live in SettingsWindow.General.cs / .Monitoring.cs / .Appearance.cs
/// (one file per nav-rail item). This file owns state, lifecycle (ctor +
/// nav + first-show fade animation), the PopulateUi() orchestrator, the
/// generic per-attribute handlers (Slider ValueChanged + GenericToggle_Click),
/// the ApplySubMeterToggle helper used by both the per-MetricCard path and
/// the direct CheckBox path, the live-update debounce timer plumbing, and
/// the footer (Save / Reset All / Quit) and Closing handlers. Visual-tree
/// helpers and the per-meter order record also live here as cross-partial
/// plumbing. All partial files declare `partial class SettingsWindow`
/// in the same WinMeters namespace, so XAML-attribute event wiring and
/// cross-partial method calls resolve transparently.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;
    private readonly AppSettings _snapshotBeforeEdit;

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
    // blocks save while this is true --- inline errors on the MetricCard
    // already call out which input is bad.
    private bool _hasValidationError;
    // Replaces the WPF Window.DialogResult property. MainWindow opens this
    // window modeless via Show() (so the user can keep fiddling with the
    // bar / drag-position it while Settings is up), and the WPF Window's
    // built-in DialogResult setter THROWS InvalidOperationException when
    // not shown via ShowDialog(). Using a plain bool works for both
    // modeless and modal Show paths and lets SettingsWindow_Closing plus
    // the MainWindow.Closed subscriber cleanly distinguish "saved" from
    // "cancelled" without touching the WPF property.
    private bool _userSaved;
    public bool WasSaved => _userSaved;

    public SettingsWindow(AppSettings original)
    {
        // Assign backing fields BEFORE InitializeComponent so the BAML
        // parser's pre-Connect event wireups (e.g. ComboBox SelectionChanged
        // via inline `SelectionChanged="..."`) find `_working` and
        // `_liveUpdateTimer` already initialised. The Slider ValueChanged
        // handlers are NOT subscribed via XAML attr (intentionally) --- the
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
        // otherwise undo the rail toggle --- that handler
        // special-cases NavRailCollapsed to preserve it across the
        // revert. We treat the rail state as window UI chrome, not
        // as a user-configurable setting, so it should survive a
        // cancel.
        _working.General.NavRailCollapsed  = collapse;
        _original.General.NavRailCollapsed = collapse;
        _original.Save();
    }

    /// <summary>
    /// First-show fade-in. Animates the window's Opacity from 0 to 1
    /// over 250ms (CubicEase EaseInOut), and if the persisted rail
    /// state is collapsed, also animates LeftRailBorder.Width from
    /// the XAML default 200px to the collapsed 48px. The user sees a
    /// smooth window-appearing animation with the rail collapsing in
    /// place if needed. Fires only once --- the handler unsubscribes
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
    /// Visibility) but NOT the Width --- the actual rail animation
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
    // UI population orchestrator
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
        // handlers during InitializeComponent --- the Slider coerce firing
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

    /// <summary>
    /// Walks the visual tree to find a WinMetersToggleSwitch CheckBox with
    /// the given Tag. Toggles on the General / Sub-Meter pages are tagged
    /// by their canonical short key (LockPosition, ShowCpuTemp, etc.); we
    /// scan descendants of the SettingsWindow so we don't have to enumerate
    /// each card positionally. Used by <c>SettingsWindow.General.cs</c>
    /// <see cref="PopulateGeneralToggles"/>.
    /// </summary>
    private WnControls.CheckBox? FindToggleByTag(string tag)
    {
        return FindVisualChildren<WnControls.CheckBox>(this)
            .FirstOrDefault(t => (t.Tag as string) == tag);
    }

    /// <summary>
    /// Generic descendant walker used by <see cref="FindToggleByTag"/> on
    /// the General page. Lives on Core so future per-section helpers
    /// (e.g. an Appearance page swatch finder) can reuse it without
    /// duplicating the recursion.
    /// </summary>
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

    // ---------------------------------------------------------------------
    // Generic toggle / slider handlers
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
            case "EnableHardwareMonitor": _working.General.EnableHardwareMonitor = value; break;
            case "CombineLogicalCores":  _working.General.CombineLogicalCores = value; break;
            // ShowCpuTemp / ShowGpuTemp / ShowHardwareLoad / ShowTime are
            // also dispatched through GenericToggle_Click because the Time
            // toggle on the Monitoring page sits in a direct CheckBox
            // (no MetricCard wrapper) and uses Click="GenericToggle_Click"
            // directly. ApplySubMeterToggle is a pure field-setter shared
            // with Card_SubMeterToggleChanged so the per-MetricCard path
            // (routed via SubMeterToggleChanged) and the direct-CheckBox
            // path stay identically synchronized. The post-switch
            // TriggerLiveUpdate below fires the debounce timer once per
            // GenericToggle_Click invocation.
            case "ShowCpuTemp":
            case "ShowGpuTemp":
            case "ShowHardwareLoad":
            case "ShowTime":
                ApplySubMeterToggle(tag, value);
                break; // fall through to post-switch TriggerLiveUpdate
        }
        TriggerLiveUpdate();
    }

    /// <summary>
    /// Pure field-setter for the four sub-meter visibility toggles
    /// (CPU Temp / GPU Temp / H/W Load / Show Time). Both the per-
    /// MetricCard path (raised from MetricCard.xaml.cs SubMeterToggleBase_Click
    /// via SubMeterToggleChanged) and the direct CheckBox path on the
    /// Monitoring page call this helper, so a new sub-meter tag only
    /// needs to be added in one place rather than two parallel switch
    /// statements. Unknown tags are silently no-op'd. Callers drive
    /// <see cref="TriggerLiveUpdate"/> themselves, which keeps each
    /// call-site's debounce semantics intact (the per-card path fires
    /// once after the helper, the per-CheckBox path falls through to
    /// the existing post-switch TriggerLiveUpdate in GenericToggle_Click).
    /// </summary>
    private void ApplySubMeterToggle(string tag, bool value)
    {
        switch (tag)
        {
            case "ShowCpuTemp":       _working.Visibility.ShowCpuTemp       = value; break;
            case "ShowGpuTemp":       _working.Visibility.ShowGpuTemp       = value; break;
            case "ShowHardwareLoad":  _working.Visibility.ShowHardwareLoad  = value; break;
            case "ShowTime":          _working.Visibility.ShowTime          = value; break;
        }
    }

    private void SliderScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _working.General.Scale = e.NewValue;
        // Live-update the right-aligned value badge. FormatScaleValue
        // lives on SettingsWindow.Appearance.cs (single source of truth
        // for the badge-string format); same-class method call resolves
        // across the partial-class files without an extra using.
        ScaleValueText.Text = FormatScaleValue(e.NewValue);
        TriggerLiveUpdate();
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _working.General.Opacity = e.NewValue;
        // Live-update the right-aligned value badge. FormatOpacityValue
        // lives on SettingsWindow.Appearance.cs (single source of truth
        // for the badge-string format); same-class method call resolves
        // across the partial-class files without an extra using.
        OpacityValueText.Text = FormatOpacityValue(e.NewValue);
        TriggerLiveUpdate();
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
        // Flag WasSaved and Close --- do NOT set WPF Window.DialogResult.
        // MainWindow shows this window modeless via Show() (see
        // MainWindow.OpenSettingsAndNavigateTo), and the WPF DialogResult
        // setter only accepts writes when the window was opened via
        // ShowDialog(). Setting it from a Show()'d window throws
        // InvalidOperationException. The MainWindow.Closed subscriber and
        // SettingsWindow_Closing both read WasSaved instead.
        _userSaved = true;
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
        // Same DialogResult caveat as BtnSave_Click: MainWindow shows this
        // window modeless via Show() so the WPF DialogResult property is
        // null on close. Read our own _userSaved bool (set by
        // BtnSave_Click) instead of the WPF property to skip the snapshot
        // revert on a successful save.
        if (_userSaved) return;

        // Preserve the nav rail collapse state across the cancel
        // revert. The standard snapshot-restore below would clobber
        // it because the snapshot predates the toggle --- but the
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

    /// <summary>
    /// Per-meter order record. The <c>MeterOrder</c> list on
    /// <c>AppSettings.General</c> stores raw keys (Cpu / GpuDedicated /
    /// GpuShared / Ram / Net / Disk / CpuTemp / GpuTemp / Time); the
    /// ListBox uses an observable collection of these items so adding,
    /// removing, and reordering produces animated list transitions.
    /// Used by the Monitoring partial file (<c>PopulateMeterOrder</c>,
    /// <c>BtnMoveUp_Click</c>, <c>BtnMoveDown_Click</c>,
    /// <c>ApplyMeterOrderToWorking</c>).
    /// </summary>
    private class MeterOrderItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-meter wiring record. Holds lambdas that read/write the three
    /// settings sub-objects (Visibility.X / Rates.X /
    /// MetricCard.SectionColorHex). Keeps PopulateMetrics / card-event
    /// handlers one-liners. Adding a new meter = one MetricBinding entry
    /// + one &lt;ctrl:MetricCard&gt; XAML element. MaxValueRemoved:
    /// WriteMaxValue / ReadMaxValue lambdas dropped in the same commit
    /// as the MetricCard Max-value TextBox removal; AppSettings.MaxValues
    /// still exists for any future consumer-side wiring.
    /// </summary>
    private sealed record MetricBinding(
        string MetricKey,
        string RateKey,
        Action<AppSettings, bool>  WriteIsShown,
        Func<AppSettings, bool>    ReadIsShown,
        Action<AppSettings, int?>  WriteRefreshRate,
        Func<AppSettings, int?>    ReadRefreshRate);
}
