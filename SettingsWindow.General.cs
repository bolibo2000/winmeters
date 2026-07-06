using System.Linq;
using WnControls = System.Windows.Controls;
using WinMeters.Monitors;

namespace WinMeters;

/// <summary>
/// Per-section partial for the **General** nav-rail tab. Owns the
/// 7 app-wide toggles (Lock Position / Hide in Fullscreen / Snap to
/// Taskbar / Keep on Top / 24-Hour Time / Hardware Telemetry / Combine
/// Logical Cores), the Refresh Rate combo, and the Hardware Selection
/// combos (Network Adapter + Disk Instance). GenericToggle_Click /
/// SliderScale_ValueChanged / SliderOpacity_ValueChanged stay on Core
/// because they're wired by XAML attribute globally; this file only
/// carries the General-specific population + SelectionChanged handlers
/// plus the visual-tree-walking FindToggleByTag helper. The 4
/// sub-meter toggles (ShowCpuTemp / ShowGpuTemp / ShowHardwareLoad /
/// ShowTime) are seeded here (and dispatched via the Core's
/// GenericToggle_Click + ApplySubMeterToggle / Monitoring's
/// Card_SubMeterToggleChanged event) since the visual-tree walker finds
/// them regardless of which tab they sit on -- they lived here
/// originally, then on per-MetricCard rows on Monitoring, then on the
/// standalone "Show Time" border on Monitoring; the helper looks
/// across the whole SettingsWindow so it doesn't care.
/// </summary>
public partial class SettingsWindow
{
    private void PopulateGeneralToggles()
    {
        var card = new[]
        {
            ("LockPosition",         _working.Window.LockPosition),
            ("HideInFullscreen",     _working.General.HideInFullscreen),
            ("SnapToTaskbar",        _working.Window.StickToTaskbar),
            ("KeepOnTop",            _working.General.KeepOnTop),
            ("Time24H",              _working.General.Time24H),
            ("EnableHardwareMonitor", _working.General.EnableHardwareMonitor),
            ("CombineLogicalCores",  _working.General.CombineLogicalCores),
        };
        foreach (var (tag, value) in card)
        {
            if (FindToggleByTag(tag) is { } ts) ts.IsChecked = value;
        }

        // Sub-meter toggles (CpuTemp / GpuTemp / HardwareLoad / Time).
        // Populated here (and not from PopulateMonitoring) because the
        // visual-tree walk finds the per-MetricCard toggles too -- they
        // are now hosted on the Monitoring page but remain visually
        // identical WinMetersToggleSwitch CheckBoxes with Tag=Show*.
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

    private void ComboRefreshRate_SelectionChanged(object sender, WnControls.SelectionChangedEventArgs e)
    {
        if (ComboRefreshRate.SelectedItem is WnControls.ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out int ms))
        {
            _working.General.RefreshRateMs = ms;
            TriggerLiveUpdate();
        }
    }

    private void PopulateDisks()
    {
        try
        {
            using var mgr = new MonitorManager();
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

    private void ComboDisk_SelectionChanged(object sender, WnControls.SelectionChangedEventArgs e)
    {
        if (ComboDisk.SelectedItem is string sel)
        {
            _working.General.DiskInstanceName = sel;
            TriggerLiveUpdate();
        }
    }

    private void ComboNetwork_SelectionChanged(object sender, WnControls.SelectionChangedEventArgs e)
    {
        if (ComboNetwork.SelectedItem is string sel)
        {
            _working.General.NetworkInterfaceName = (sel == "(All Interfaces)") ? null : sel;
            TriggerLiveUpdate();
        }
    }
}
