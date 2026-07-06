using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WnControls = System.Windows.Controls;
#if !DESIGN_TIME
using WnForms = System.Windows.Forms;
#endif

namespace WinMeters;

/// <summary>
/// Per-section partial for the **Appearance** nav-rail tab plus the
/// **About** subsection. Owns the Scale + Opacity sliders (and their
/// live value badges), the 3 theme-token color pickers
/// (Accent / Background / Border) using Windows.Forms.ColorDialog,
/// the About-section version header (read from FileVersionInfo at
/// startup), and PopulateNavTooltips / NavItemMetadata which build
/// the hover-ToolTip content for the 5 nav RadioButtons (the ToolTips
/// themselves live on the global Theme). The slider ValueChanged
/// handlers themselves stay on Core so they share the live-update
/// plumbing with all other XAML-attribute handlers; only their value
/// formatting + initial value text-set happens here.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>
    /// Single source of truth for the nav item display name + the
    /// one-line description shown in the hover ToolTip on the 5
    /// nav RadioButtons (built in <see cref="PopulateNavTooltips"/>).
    /// If a 6th nav item lands here, both the XAML nav-item section
    /// and this table need editing.
    /// </summary>
    private static readonly (string Name, string Description)[] NavItemMetadata = new[]
    {
        ("Home",       "Overview and quick links"),
        ("General",    "Lock position, hide in fullscreen, refresh rate"),
        ("Monitoring", "Per-meter show / max-value / refresh-rate / colour"),
        ("Appearance", "Scale, opacity, theme accent, background, border"),
        ("About",      "Version info and project links"),
    };

    private void PopulateAppearance()
    {
        SliderScale.Value   = _working.General.Scale;
        SliderOpacity.Value = _working.General.Opacity;

        // Seed the value badges alongside the slider sets. These run
        // BEFORE the SliderScale/SliderOpacity ValueChanged handlers
        // are subscribed at the end of PopulateUi() (intentional - the
        // handler-isn't-subscribed-yet invariant keeps the Slider coerce
        // during InitializeComponent from clobbering the saved value).
        // After this, the badges stay in sync via SliderScale_ValueChanged
        // / SliderOpacity_ValueChanged on Core, both of which call the
        // shared Format* helpers below so the seed and the live update
        // can't disagree if either side is tweaked later.
        ScaleValueText.Text   = FormatScaleValue(_working.General.Scale);
        OpacityValueText.Text = FormatOpacityValue(_working.General.Opacity);

        SetSwatch(SwatchAccent,     _working.Colors.Accent);
        SetSwatch(SwatchBackground, _working.Colors.Background);
        SetSwatch(SwatchBorder,     _working.Colors.Border);
    }

    /// <summary>
    /// Single source of truth for the Scale slider's value-badge
    /// formatting ("0.0\u00d7" with InvariantCulture). Called both at
    /// populate-seed time (PopulateAppearance above) and on every
    /// SliderScale_ValueChanged tick from Core, so the badge never drifts
    /// from the slider position. Explicit InvariantCulture keeps the
    /// decimal separator consistent across comma-decimal locales.
    /// </summary>
    private static string FormatScaleValue(double v) =>
        v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "\u00d7";

    /// <summary>
    /// Single source of truth for the Opacity slider's value-badge
    /// formatting ("0%" with InvariantCulture). Called both at
    /// populate-seed time (PopulateAppearance above) and on every
    /// SliderOpacity_ValueChanged tick from Core, so the badge never
    /// drifts from the slider position. Translucency is rendered as
    /// integer percent (the slider's internal 0.0-1.0 is multiplied
    /// by 100) since UI users expect whole-number percent for the
    /// translucency knob rather than fractional precision.
    /// </summary>
    private static string FormatOpacityValue(double v) =>
        (v * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

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
    /// Build rich hover ToolTips on the 5 nav RadioButtons from
    /// <see cref="NavItemMetadata"/>. The ToolTip uses the global
    /// WinMetersTooltip style (dark card surface, drop shadow); we
    /// just provide the content (bold section name + one-line
    /// description). Single source of truth for the description text.
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

    private static void SetSwatch(Border swatch, string hex)
    {
        swatch.Background = ColorHelper.ParseBrush(hex);
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
}
