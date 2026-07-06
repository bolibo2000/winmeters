using System.Collections.ObjectModel;
using System.Windows;
using WnControls = System.Windows.Controls;
using WinMeters.Controls;

namespace WinMeters;

/// <summary>
/// Per-section partial for the **Monitoring** nav-rail tab. Owns the
/// 5 MetricCard instances (Cpu / Ram / Gpu / Net / Disk) plus the
/// Meter Display Order list (per-card reorder via Move Up / Move Down
/// buttons) and the per-card event handlers (IsShownChanged /
/// RefreshRateChanged / SectionColorChanged / SubMeterToggleChanged /
/// ValidationFailed). Static MetricBindings / MetricCardsByKey /
/// MeterDisplayNames live here because they're tightly coupled to the
/// card lifecycle (PopulateMetrics wires events from the static list).
/// The MetricBinding record itself is on the Core file so card-event
/// handlers can declare the row type via the enclosing class without
/// a circular dependency.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>
    /// Static metric-key -> AppSettings wiring helper. Each entry tells the
    /// populate / save loops which sub-object holds the meter state. Adding
    /// a new meter means adding one entry here + one &lt;ctrl:MetricCard&gt;
    /// x:Name to the XAML.
    /// MaxValueRemoved: the WriteMaxValue / ReadMaxValue lambda pair was
    /// dropped from every entry along with the per-meter Max-value TextBox
    /// on MetricCard.xaml in the same commit (user request: remove the
    /// Monitoring Max-value option from UI). The AppSettings.MaxValues
    /// fields stay intact so any future consumer-side wiring can drop in
    /// without touching settings.json.
    /// </summary>
    private static readonly MetricBinding[] MetricBindings =
    {
        new("Cpu",  "Cpu",      // MetricKey, Rate-binding key in AppSettings.Rates
            (s, v) => s.Visibility.ShowCpu = v,
            (s) => s.Visibility.ShowCpu,
            (s, v) => s.Rates.Cpu = v,
            (s) => s.Rates.Cpu),
        new("Ram",  "Ram",
            (s, v) => s.Visibility.ShowRam = v,
            (s) => s.Visibility.ShowRam,
            (s, v) => s.Rates.Ram = v,
            (s) => s.Rates.Ram),
        // Gpu card aggregates ShowGpuDedicated + ShowGpuShared into one
        // toggle. The MetricCard's IsShown boolean reflects "any GPU pie
        // shown"; we OR the two on save.
        new("Gpu",  "GpuDedicated",
            (s, v) => { if (v) { s.Visibility.ShowGpuDedicated = true; s.Visibility.ShowGpuShared = true; } else { s.Visibility.ShowGpuDedicated = false; s.Visibility.ShowGpuShared = false; } },
            (s) => s.Visibility.ShowGpuDedicated && s.Visibility.ShowGpuShared,
            (s, v) => s.Rates.GpuDedicated = v,
            (s) => s.Rates.GpuDedicated),
        new("Net",  "Net",
            (s, v) => s.Visibility.ShowNet = v,
            (s) => s.Visibility.ShowNet,
            (s, v) => s.Rates.Net = v,
            (s) => s.Rates.Net),
        new("Disk", "Disk",
            (s, v) => s.Visibility.ShowDisk = v,
            (s) => s.Visibility.ShowDisk,
            (s, v) => s.Rates.Disk = v,
            (s) => s.Rates.Disk),
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
            card.RefreshRateText = (binding.ReadRefreshRate(_working) ?? _working.General.RefreshRateMs).ToString();

            if (_working.SectionColors.TryGetValue(binding.MetricKey, out var hex))
                card.SectionColorHex = hex;

            // Idempotent event attach. unsubscribe-then-subscribe - never
            // double-tap on subsequent PopulateUi calls (BIND-77x redo).
            // MaxValueChanged was removed alongside the per-meter Max-value
            // TextBox on MetricCard.xaml in the same commit (user request:
            // remove the Monitoring Max-value option from UI).
            // SubMeterToggleChanged is wired here so each MetricCard that
            // hosts sub-meter toggles (Cpu -> CPU Temp / H/W Load, Gpu ->
            // GPU Temp) can raise toggle clicks out to this handler.
            card.IsShownChanged         -= Card_IsShownChanged;
            card.RefreshRateChanged     -= Card_RefreshRateChanged;
            card.SectionColorChanged    -= Card_SectionColorChanged;
            card.ValidationFailed       -= Card_ValidationFailed;
            card.SubMeterToggleChanged  -= Card_SubMeterToggleChanged;

            card.IsShownChanged         += Card_IsShownChanged;
            card.RefreshRateChanged     += Card_RefreshRateChanged;
            card.SectionColorChanged    += Card_SectionColorChanged;
            card.ValidationFailed       += Card_ValidationFailed;
            card.SubMeterToggleChanged  += Card_SubMeterToggleChanged;
        }
    }

    private void ListMeterOrder_SelectionChanged(object sender, WnControls.SelectionChangedEventArgs e)
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

    private void ApplyMeterOrderToWorking()
    {
        if (ListMeterOrder.ItemsSource is not ObservableCollection<MeterOrderItem> list) return;
        var newOrder = new List<string>();
        foreach (var item in list) newOrder.Add(item.Key);
        _working.General.MeterOrder = newOrder;
    }

    private void Card_IsShownChanged(object? sender, EventArgs e)
    {
        if (sender is not MetricCard card) return;
        var binding = FindBinding(card.MetricKey);
        if (binding is null) return;
        binding.WriteIsShown(_working, card.IsShown);
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

    /// <summary>
    /// Per-MetricCard sub-meter toggle handler. The sub-meter toggles
    /// (CPU Temp / GPU Temp / H/W Load) used to live in the General
    /// section's UniformGrid; in the re-home commit they moved into
    /// per-card inline rows on the Monitoring page (Cpu card shows CPU
    /// Temp + H/W Load, Gpu card shows GPU Temp). The GenericToggle_Click
    /// handler resolves only against SettingsWindow.xaml, so the cards
    /// wire their inline Click to MetricCard.xaml.cs SubMeterToggleBase_Click
    /// which raises <see cref="SubMeterToggleChangedEventArgs"/>. We then
    /// dispatch through the shared ApplySubMeterToggle helper so the
    /// per-card path stays synchronized with the direct CheckBox path
    /// (the Show Time toggle on the Monitoring page uses GenericToggle_Click
    /// directly).
    /// </summary>
    private void Card_SubMeterToggleChanged(object? sender, SubMeterToggleChangedEventArgs e)
    {
        ApplySubMeterToggle(e.Tag, e.IsChecked);
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
}
