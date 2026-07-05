using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinMeters;

public class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public ColorSettings Colors { get; set; } = new();
    public VisibilitySettings Visibility { get; set; } = new();
    public RateSettings Rates { get; set; } = new();
    public MaxValueSettings MaxValues { get; set; } = new();
    // Kil0bit-style per-meter section color. One swatch per meter (driven
    // by the new MetricCard on the Monitoring page) replaces the legacy
    // 14 swatches that lived on the Appearance page. Defaults were chosen
    // for maximum hue separation on a dark ThemeBgBrush background.
    public Dictionary<string, string> SectionColors { get; set; } = new(StringComparer.Ordinal)
    {
        ["Cpu"]  = "#FFFF6B6B",
        ["Ram"]  = "#FF4ECDC4",
        ["Gpu"]  = "#FF95E1D3",
        ["Net"]  = "#FF6C5CE7",
        ["Disk"] = "#FFFFEAA7",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string BaseDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? AppDomain.CurrentDomain.BaseDirectory;
    private static string SettingsPath => Path.Combine(BaseDir, Constants.Files.SettingsFileName);
    private static string BackupPath => Path.Combine(BaseDir, Constants.Files.SettingsBackupFileName);

    public static AppSettings Load()
    {
        var settings = TryLoadFromFile(SettingsPath);
        if (settings is not null) return settings;

        WinMeters.Log.D("Load: Primary settings failed, trying backup...");
        settings = TryLoadFromFile(BackupPath);
        if (settings is not null)
        {
            settings.Save();
            return settings;
        }

        WinMeters.Log.D("Load: No valid settings found, creating defaults.");
        var defaults = new AppSettings();
        defaults.Save();
        return defaults;
    }

    private static AppSettings? TryLoadFromFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is not null)
                MigrateSettings(settings, json);
            return settings;
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"TryLoadFromFile '{path}': {ex.Message}");
            return null;
        }
    }

    private static void MigrateSettings(AppSettings settings, string rawJson)
    {
        // New MaxValues / SectionColors keys introduced with the MetricCard
        // refactor. Older settings.json files lack these tokens so they keep
        // their existing defaults. Visibility/Rate/Color legacy fields are
        // untouched — MeterCard reads from them backward-compatibly.
        if (!Has(rawJson, "MaxValues"))
        {
            settings.MaxValues = new MaxValueSettings();
        }
        if (!Has(rawJson, "SectionColors"))
        {
            settings.SectionColors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Cpu"]  = "#FFFF6B6B",
                ["Ram"]  = "#FF4ECDC4",
                ["Gpu"]  = "#FF95E1D3",
                ["Net"]  = "#FF6C5CE7",
                ["Disk"] = "#FFFFEAA7",
            };
        }

        // Migrate legacy MeterOrder keys (older versions used human-readable labels)
        // to the canonical short keys the rest of the app uses.
        var legacyKey = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cpu Temp"] = "CpuTemp",
            ["Gpu Temp"] = "GpuTemp",
            ["VRAM Pie"] = "GpuDedicated",
            ["SRAM Pie"] = "GpuShared",
            ["Ram Pie"] = "Ram",
            ["Disk Pie"] = "Disk",
        };
        for (int i = 0; i < settings.General.MeterOrder.Count; i++)
        {
            settings.General.MeterOrder[i] = legacyKey.TryGetValue(settings.General.MeterOrder[i], out var mapped)
                ? mapped
                : settings.General.MeterOrder[i];
        }

        // Deduplicate the list to ensure clean settings.
        settings.General.MeterOrder = settings.General.MeterOrder.Distinct().ToList();

        // Apply defaults for fields absent from the JSON — indicates an older settings file.
        static bool Has(string raw, string token) => raw.Contains(token, StringComparison.OrdinalIgnoreCase);

        if (!Has(rawJson, "ShowGpuDedicated")) settings.Visibility.ShowGpuDedicated = true;
        if (!Has(rawJson, "ShowGpuShared"))    settings.Visibility.ShowGpuShared    = true;

        EnsureMeterOrderEntry(settings.General.MeterOrder, "GpuDedicated", afterKey: "GpuTemp");
        EnsureMeterOrderEntry(settings.General.MeterOrder, "GpuShared",    afterKey: "GpuDedicated");

        if (!Has(rawJson, "GpuDedicatedPie")) settings.Colors.GpuDedicatedPie = "#4ECDC4";
        if (!Has(rawJson, "GpuSharedPie"))    settings.Colors.GpuSharedPie    = "#A5D6A7";

        if (!Has(rawJson, "ShowTime"))  settings.Visibility.ShowTime  = true;
        if (!Has(rawJson, "Time24H"))   settings.General.Time24H       = true;
        if (!Has(rawJson, "TimeText"))  settings.Colors.TimeText       = "#FFD54F";

        EnsureMeterOrderEntry(settings.General.MeterOrder, "Time", afterKey: null);

        // StickToTaskbar / monitor migration — old files used DockOnTaskbar + WindowMode;
        // honour the legacy DockOnTaskbar where present and default to "stuck" otherwise
        // so first-launch behaviour matches the prior AppBar default.
        if (Has(rawJson, "DockOnTaskbar"))
        {
            // The user had an explicit preference encoded as DockOnTaskbar in the legacy
            // JSON; mirror it into the new field. Bool.TryParse keeps us safe against
            // hand-edited files that carry a non-boolean token here.
            bool legacyDock = TryReadBool(rawJson, "DockOnTaskbar", defaultValue: true);
            settings.Window.StickToTaskbar = legacyDock;
        }
        if (Has(rawJson, "WindowMode"))
        {
            // "Floating" => unstick; anything else (including "AppBar") => stuck.
            // Default to stuck if we cannot parse the token so we match the kil0bit default.
            string legacyMode = TryReadString(rawJson, "WindowMode") ?? "AppBar";
            bool legacyFloating = legacyMode.Equals("Floating", StringComparison.OrdinalIgnoreCase);
            // Only override if DockOnTaskbar wasn't also present (DockOnTaskbar wins if both).
            if (!Has(rawJson, "DockOnTaskbar"))
                settings.Window.StickToTaskbar = !legacyFloating;
        }
        if (!Has(rawJson, "MonitorIndex"))
            settings.Window.MonitorIndex = 0;
        // Clamp a bogus value from a corrupted settings file rather than crash later.
        if (settings.Window.MonitorIndex < 0)
            settings.Window.MonitorIndex = 0;
    }

    /// <summary>
    /// Best-effort bool read from a raw JSON token. Falls back to
    /// <paramref name="defaultValue"/> when the token is missing or not a clean
    /// JSON boolean. Walks a 32-char window after the key so we don't pick up
    /// accidental substrings.
    /// </summary>
    private static bool TryReadBool(string raw, string key, bool defaultValue)
    {
        int idx = raw.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return defaultValue;
        // Walk past the key + colon and any whitespace to find the literal.
        int scan = idx + key.Length;
        while (scan < raw.Length && (raw[scan] == ':' || char.IsWhiteSpace(raw[scan]))) scan++;
        if (scan >= raw.Length) return defaultValue;
        if (raw[scan] == 't' || raw[scan] == 'T') return true;
        if (raw[scan] == 'f' || raw[scan] == 'F') return false;
        return defaultValue;
    }

    /// <summary>
    /// Naive string-token read for legacy <c>WindowMode</c> etc. Looks for
    /// <c>"&lt;key&gt;" : "&lt;value&gt;"</c> and returns the value, or
    /// <c>null</c> if no clean match exists.
    /// </summary>
    private static string? TryReadString(string raw, string key)
    {
        string token = $"\"{key}\"";
        int idx = raw.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return null;
        int colon = raw.IndexOf(':', idx + token.Length);
        if (colon < 0) return null;
        int firstQuote = raw.IndexOf('"', colon + 1);
        if (firstQuote < 0) return null;
        int secondQuote = raw.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return null;
        return raw.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    private static void EnsureMeterOrderEntry(List<string> order, string key, string? afterKey)
    {
        if (order.Contains(key)) return;

        int insertAt;
        if (afterKey is null)
        {
            insertAt = order.Count;
        }
        else
        {
            // -1 + 1 = 0 won't fire here because null branch was taken first.
            insertAt = order.IndexOf(afterKey) + 1;
            if (insertAt <= 0) insertAt = order.Count;
        }
        order.Insert(insertAt, key);
    }

    public void Save()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                try { File.Copy(SettingsPath, BackupPath, overwrite: true); }
                catch (Exception ex) { WinMeters.Log.D($"Save: Failed to create backup: {ex.Message}"); }
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"Save: failed to write settings: {ex}");
        }
    }

    public class GeneralSettings
    {
        public int RefreshRateMs { get; set; } = 1000;
        public double Opacity { get; set; } = 1.0;
        public double Scale { get; set; } = 1.0;
        /// <summary>
        /// Kil0bit-style “Keep on Top” toggle. Drives <c>this.Topmost</c> on the Bar's
        /// Kil0bit-style “Keep on Top” toggle. Drives <c>this.Topmost</c> on the Bar's
        /// WPF Window and gates the float-mode EnforceZOrder timer. Mirrors kil0bit's
        /// <c>_config.Config.AlwaysOnTop</c>; default true to preserve WinMeters's
        /// pre-toggle behaviour (TOPMOST + periodic re-assertion).
        /// </summary>
        public bool KeepOnTop { get; set; } = true;
        /// <summary>
        /// Kil0bit-style “Hide in Fullscreen” toggle. When true and a fullscreen app
        /// is the foreground, the bar is collapsed. Mirror kil0bit's
        /// <c>_config.Config.HideOnFullscreen</c>. Dock-mode fullscreen detection
        /// rides on the existing AppBarService ABN_FULLSCREENAPP handler; floating
        /// mode rides on a WM_ACTIVATEAPP hook in MainWindow (borderless-maximised
        /// detection -- same heuristic as kil0bit).
        /// </summary>
        public bool HideInFullscreen { get; set; } = false;
        public string? NetworkInterfaceName { get; set; }
        public string DiskInstanceName { get; set; } = "_Total";
        public bool CombineLogicalCores { get; set; } = true;
        public List<string> MeterOrder { get; set; } = ["Cpu", "CpuTemp", "GpuTemp", "GpuDedicated", "GpuShared", "Ram", "Disk", "Net", "Time"];
        public bool EnableHardwareMonitor { get; set; } = true;
        public bool Time24H { get; set; } = true;
    }

    public class WindowSettings
    {
        public bool LockPosition { get; set; } = false;
        /// <summary>
        /// Single kil0bit-style flag. When <c>true</c> the window is parented to the
        /// shell taskbar (registered as an appbar) and Y-centring is enforced inside
        /// the WndProc whenever the shell repositions us. When <c>false</c> the bar
        /// is a free-floating window whose X/Y are honoured verbatim.
        /// </summary>
        public bool StickToTaskbar { get; set; } = true;
        public double? PositionX { get; set; }
        public double? PositionY { get; set; }
        public double Height { get; set; }
        public bool IsHiddenByUser { get; set; }
        /// <summary>
        /// Legacy monitor hint. Not consumed by the post-migration code path —
        /// kept only so older settings.json files load without losing data. New
        /// installs rely on whatever monitor the window is currently on, matching
        /// kil0bit's behaviour.
        /// </summary>
        public int MonitorIndex { get; set; } = 0;
    }

    public class ColorSettings
    {
        /// <summary>
        /// Accent color used for kil0bit-style UI affordances: focused nav-rail item,
        /// ToggleSwitch track (on), button accents, link underline. Mirrors kil0bit's
        /// <c>Config.AccentColorHex</c>; default #00CCFF (kil0bit's default accent).
        /// Editing this updates the theme; live-preview re-applies on the open dialog.
        /// </summary>
        public string Accent { get; set; } = "#00CCFF";
        public string Background { get; set; } = "#FF202020";
        public string Border { get; set; } = "#44FFFFFF";
        public double BorderThickness { get; set; }
        public double CpuBorderThickness { get; set; } = 0.5;
        public double RamBorderThickness { get; set; } = 0.5;
        public string Separator { get; set; } = "#00FFFFFF";
        public string Label { get; set; } = "#AAAAAA";
        public string CpuSys { get; set; } = "#44FF44";
        public string CpuUser { get; set; } = "#FF4444";
        public string CpuTrack { get; set; } = "#28FFFFFF";
        public string RamPie { get; set; } = "#FFA500";
        public string RamBorder { get; set; } = "#FF000000";
        public string DiskRead { get; set; } = "#44FF44";
        public string DiskWrite { get; set; } = "#FF4444";
        public string NetDown { get; set; } = "#44FF44";
        public string NetUp { get; set; } = "#FF4444";
        public string CpuTemp { get; set; } = "#ffae6bff";
        public string GpuTemp { get; set; } = "#4ECDC4";
        public string GpuDedicatedPie { get; set; } = "#4ECDC4";
        public string GpuSharedPie { get; set; } = "#A5D6A7";
        public string TimeText { get; set; } = "#FFD54F";
    }

    public class VisibilitySettings
    {
        public bool ShowCpu { get; set; } = true;
        public bool ShowRam { get; set; } = true;
        public bool ShowDisk { get; set; } = true;
        public bool ShowNet { get; set; } = true;
        public bool ShowCpuTemp { get; set; } = true;
        public bool ShowGpuTemp { get; set; } = true;
        public bool ShowHardwareLoad { get; set; } = true;
        public bool ShowGpuDedicated { get; set; } = true;
        public bool ShowGpuShared { get; set; } = true;
        public bool ShowTime { get; set; } = true;
    }

    public class RateSettings
    {
        public int? Cpu { get; set; }
        public int? Ram { get; set; }
        public int? Disk { get; set; }
        public int? Net { get; set; }
        public int? CpuTemp { get; set; }
        public int? GpuTemp { get; set; }
        public int? GpuDedicated { get; set; }
        public int? GpuShared { get; set; }
    }

    /// <summary>
    /// Per-meter max-value pairs (one per logical meter) that normalise the
    /// bar's normalised fill. CPU/RAM/GPU are percentage-bounded (default
    /// 100); Net and Disk are absolute (KB/s) and start at zero so the user
    /// can grow them via the new MetricCard MaxValue textbox on the
    /// Monitoring page.
    /// </summary>
    public class MaxValueSettings
    {
        public double Cpu { get; set; } = 100;
        public double Ram { get; set; } = 100;
        public double Gpu { get; set; } = 100;
        public double Net { get; set; } = 0;
        public double Disk { get; set; } = 0;
    }
}
