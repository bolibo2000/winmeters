using System.Globalization;
using System.Windows;
using WpfCtrl = System.Windows.Controls;
using WpfMedia = System.Windows.Media;
using System.Windows.Input;
#if !DESIGN_TIME
using WnForms = System.Windows.Forms;
#endif

namespace WinMeters.Controls;    /// <summary>
    /// One themed per-meter card with three controls: Show toggle, Section
    /// colour swatch, Refresh-rate textbox. Replaces the flat UniformGrids
    /// that previously scattered the same affordances across the Monitoring,
    /// General, and Appearance pages. Adding a new meter means wiring one
    /// new card here on the Monitoring page -- not three pages of switch
    /// statements. WinMeters-style dark surface; the in-card Show toggle
    /// uses the hand-built WinMetersToggleSwitch style (a CheckBox
    /// retemplated into a 40x20 rounded track + sliding thumb).
    /// MaxValueRemoved: the per-meter Max-value textbox was removed from
    /// this card in lock-step with its DependencyProperty, validation
    /// branch, MaxValueChanged event, and MaxValueChangedEventArgs class
    /// (user request: remove the Monitoring Max-value option from UI).
    /// AppSettings.MaxValues still exists so any future consumer-side
    /// wiring can drop in without touching settings.json.
///
/// Type-name disambiguation: the WinMeters project has both UseWPF=true
/// and UseWindowsForms=true so the SDK auto-imports both
/// System.Windows.Controls and System.Windows.Forms namespace prefixes.
/// Type names shared between the two (UserControl, TextBox, Brush,
/// TextBlock) would otherwise be CS0104-ambiguous. We resolve by routing
/// through WpfCtrl / WpfMedia aliases below; native WinForms types stay
/// behind the WnForms prefix alias where needed.
/// </summary>
public partial class MetricCard : WpfCtrl.UserControl
{
    public MetricCard()
    {
        InitializeComponent();

        // Idempotent PreviewTextInput attach for the refresh-rate text box.
        // Detach-then-attach avoids double-subscribing if PopulateUi uses
        // the same card instance twice (BIND-77x family of regressions).
        // (The Max-value text box was removed alongside the MaxValue UI
        // affordance; see the row-2 comment in MetricCard.xaml.)
        TxtRate.PreviewTextInput  -= Numeric_PreviewTextInput;
        TxtRate.PreviewTextInput  += Numeric_PreviewTextInput;
    }

    // ---- DependencyProperties ---------------------------------------------------

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Canonical short key (Cpu / Ram / Gpu / Net / Disk).</summary>
    public static readonly DependencyProperty MetricKeyProperty =
        DependencyProperty.Register(nameof(MetricKey), typeof(string), typeof(MetricCard),
            new PropertyMetadata(string.Empty));

    public string MetricKey
    {
        get => (string)GetValue(MetricKeyProperty);
        set => SetValue(MetricKeyProperty, value);
    }

    public static readonly DependencyProperty IsShownProperty =
        DependencyProperty.Register(nameof(IsShown), typeof(bool), typeof(MetricCard),
            new PropertyMetadata(true, OnIsShownChanged));

    public bool IsShown
    {
        get => (bool)GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    public static readonly DependencyProperty RefreshRateTextProperty =
        DependencyProperty.Register(nameof(RefreshRateText), typeof(string), typeof(MetricCard),
            new PropertyMetadata("1000", OnNumericTextChanged));

    public string RefreshRateText
    {
        get => (string)GetValue(RefreshRateTextProperty);
        set => SetValue(RefreshRateTextProperty, value);
    }

    public static readonly DependencyProperty SectionColorHexProperty =
        DependencyProperty.Register(nameof(SectionColorHex), typeof(string), typeof(MetricCard),
            new PropertyMetadata("#FF00CCFF", OnSectionColorChanged));

    public string SectionColorHex
    {
        get => (string)GetValue(SectionColorHexProperty);
        set => SetValue(SectionColorHexProperty, value);
    }

    /// <summary>
    /// Resolved brush from <see cref="SectionColorHex"/>. Bound by the Swatch
    /// element as a fallback if the consumer didn't bind SectionColorHex
    /// directly to a parsed brush.
    /// </summary>
    public WpfMedia.Brush SectionColorBrush
    {
        get
        {
            try
            {
                return ColorHelper.ParseBrush(SectionColorHex) ?? WpfMedia.Brushes.Gray;
            }
            catch
            {
                return WpfMedia.Brushes.Gray;
            }
        }
    }

    // ---- Public events raised when the user changes a value --------------------

    public event EventHandler? IsShownChanged;
    // SubMeterMoved: the 3 sub-meter visibility toggles (CPU Temp / GPU
    // Temp / H/W Load) used to live on the General page in a 4-column
    // UniformGrid. They are now inline per-MetricCard toggles (Cpu card
    // shows CPU Temp + H/W Load, Gpu card shows GPU Temp). The toggle's
    // Click handler is routed here via SubMeterToggleBase_Click; the
    // handler raises this event so the parent SettingsWindow can
    // subscribe once per card and dispatch the tag through the same
    // switch as GenericToggle_Click.
    // MaxValueRemoved: the per-meter Max-value TextBox was removed from
    // MetricCard.xaml in the same commit (user request: remove the
    // Monitoring Max-value option from UI). The MaxValues data model on
    // AppSettings stays intact so any future consumer-side wiring can
    // drop in without touching settings.json. The MaxValueChanged event,
    // ValidationFailedEventArgs.MaxError, the TryParseDouble branch, and
    // the MaxText preview filter were all deleted in lock-step with the
    // UI affordance.
    public event EventHandler<RefreshRateChangedEventArgs>? RefreshRateChanged;
    public event EventHandler<SectionColorChangedEventArgs>? SectionColorChanged;
    public event EventHandler<ValidationFailedEventArgs>? ValidationFailed;
    public event EventHandler<SubMeterToggleChangedEventArgs>? SubMeterToggleChanged;

    // ---- Property changed callbacks -------------------------------------------

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetricCard c) c.IsShownChanged?.Invoke(c, EventArgs.Empty);
    }

    private static void OnNumericTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetricCard c) c.ValidateAndFire();
    }

    private static void OnSectionColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetricCard c)
        {
            // SectionColorBrush is a CLR computed property; XAML binding to it
            // does not re-fire on hex changes. We write the swatch background
            // directly here. The SectionColorChanged event still fires so
            // SettingsWindow can persist the new hex to AppSettings.
            c.Swatch.Background = ColorHelper.ParseBrush(c.SectionColorHex) ?? WpfMedia.Brushes.Gray;
            c.SectionColorChanged?.Invoke(c, new SectionColorChangedEventArgs(c.SectionColorHex));
        }
    }

    // ---- Validation ----------------------------------------------------------

    private void ValidateAndFire()
    {
        bool rateOk = TryParseInt(RefreshRateText, out int rateValue, out string? rateErr);
        if (rateErr is not null)
        {
            ShowError(ErrRate, TxtRate, rateErr);
        }
        else
        {
            ClearError(ErrRate, TxtRate);
            RefreshRateChanged?.Invoke(this, new RefreshRateChangedEventArgs(MetricKey, rateValue));
        }
        if (!rateOk)
        {
            ValidationFailed?.Invoke(this, new ValidationFailedEventArgs(MetricKey, rateErr));
        }
    }

    private void ShowError(WpfCtrl.TextBlock err, WpfCtrl.TextBox tb, string msg)
    {
        err.Text = msg;
        err.Visibility = Visibility.Visible;
        tb.BorderBrush = (WpfMedia.Brush?)FindResource("ThemeDangerBrush");
        tb.ToolTip = msg;
    }

    private void ClearError(WpfCtrl.TextBlock err, WpfCtrl.TextBox tb)
    {
        err.Text = string.Empty;
        err.Visibility = Visibility.Collapsed;
        tb.ClearValue(BorderBrushProperty);
        tb.ToolTip = null;
    }

    private static bool TryParseInt(string? s, out int value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(s)) { value = 0; error = "Empty"; return false; }
        if (!int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        { error = "Invalid number"; return false; }
        if (value < 50) { error = "Minimum 50 ms"; return false; }
        error = null;
        return true;
    }

    private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Digit-only filter for the refresh-rate text box. Match the legacy
        // single-page dialog filter so users can type "1000" without pasting
        // in a unit suffix. We accept "." only (not ",") because the validator
        // parses with InvariantCulture; accepting "1,5" only to have the
        // validator reject it would be confusing.
        e.Handled = !string.IsNullOrEmpty(e.Text) &&
                    !e.Text.All(ch => char.IsDigit(ch) || ch == '.');
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
#if !DESIGN_TIME
        try
        {
            using var dlg = new WnForms.ColorDialog
            {
                FullOpen = true,
                Color = ColorHelper.ToDrawingColor(SectionColorHex)
            };
            if (dlg.ShowDialog() != WnForms.DialogResult.OK) return;

            string alpha = "FF";
            if (SectionColorHex is { Length: 9 } hex9) alpha = hex9.Substring(1, 2);
            string next = $"#{alpha}{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            // DP setter will fire OnSectionColorChanged, which writes the Swatch
            // brush and raises SectionColorChanged for the parent to persist.
            SectionColorHex = next;
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"MetricCard.ColorButton_Click: {ex.Message}");
        }
#endif
    }

    /// <summary>
    /// Click handler for the per-card sub-meter toggles defined in
    /// MetricCard.xaml Row 3 (CPU Temp / GPU Temp / H/W Load). The
    /// SettingsWindow.xaml.cs GenericToggle_Click handler resolves
    /// only against SettingsWindow.xaml, not against MetricCard.xaml,
    /// so we can't wire directly. Routed via a Click event here on
    /// MetricCard.xaml.cs that reads the ToggleSwitch's Tag and
    /// forwards it through <see cref="SubMeterToggleChanged"/> so
    /// SettingsWindow can dispatch the same way it does for its
    /// own direct toggles.
    /// </summary>
    private void SubMeterToggleBase_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfCtrl.CheckBox ts || ts.Tag is not string tag) return;
        SubMeterToggleChanged?.Invoke(this, new SubMeterToggleChangedEventArgs(tag, ts.IsChecked == true));
    }
}

public class RefreshRateChangedEventArgs : EventArgs
{
    public string MetricKey { get; }
    public int Value { get; }
    public RefreshRateChangedEventArgs(string key, int value) { MetricKey = key; Value = value; }
}

public class SubMeterToggleChangedEventArgs : EventArgs
{
    /// <summary>The toggle's Tag -- mirrors the AppSettings.Visibility key the toggle writes through to (e.g. ShowCpuTemp). MetricKey was previously carried on the args but the lone consumer (SettingsWindow.Card_SubMeterToggleChanged) dispatches purely on Tag, so it was removed to reduce surface area.</summary>
    public string Tag { get; }
    public bool IsChecked { get; }
    public SubMeterToggleChangedEventArgs(string tag, bool isChecked)
    {
        Tag = tag; IsChecked = isChecked;
    }
}

public class SectionColorChangedEventArgs : EventArgs
{
    public string Hex { get; }
    public SectionColorChangedEventArgs(string hex) { Hex = hex; }
}

public class ValidationFailedEventArgs : EventArgs
{
    public string MetricKey { get; }
    public string? RateError { get; }
    public ValidationFailedEventArgs(string key, string? rateErr)
    {
        MetricKey = key; RateError = rateErr;
    }
}
