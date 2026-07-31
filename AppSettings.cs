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
        if (settings is not null) { settings.Save(); return settings; }

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
            if (settings is not null) MigrateSettings(settings, json);
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
        // We still use a fast substring probe for the cheap "is this property present at all"
        // gate (cheap; short-circuits the bulk of newer-field checks on modern saves) but the
        // actual *value* extraction below now goes through JsonDocument. The previous hand-rolled
        // TryReadBool / TryReadString were IndexOf/substring scans that mishandled escaped quotes
        // or a hex value happening to contain an int token inside it.
        using var doc = InitializeJsonDocument(rawJson);
        // TryGetProperty on a default(JsonElement) throws InvalidOperationException, so guard
        // every structured read with the same `canReadStructured` predicate. The substring
        // `Has(rawJson, ...)` checks below are still safe even when doc is null. Note the
        // substring path can no-op cleanly (e.g. it never shadows DefaultSectionColors /
        // MaxValues territory either) when structured data is unavailable; rolled up there.
        bool canReadStructured = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object;
        var root = doc is null ? default : doc.RootElement;
        var colors = canReadStructured && root.TryGetProperty("Colors", out var cEl) && cEl.ValueKind == JsonValueKind.Object ? cEl : default;
        var window = canReadStructured && root.TryGetProperty("Window", out var wEl) && wEl.ValueKind == JsonValueKind.Object ? wEl : default;

        static bool Has(string raw, string token) => raw.Contains(token, StringComparison.OrdinalIgnoreCase);

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
                ? mapped : settings.General.MeterOrder[i];
        }
        settings.General.MeterOrder = settings.General.MeterOrder.Distinct().ToList();

        if (!Has(rawJson, "ShowGpuDedicated")) settings.Visibility.ShowGpuDedicated = true;
        if (!Has(rawJson, "ShowGpuShared"))    settings.Visibility.ShowGpuShared    = true;

        EnsureMeterOrderEntry(settings.General.MeterOrder, "GpuDedicated", afterKey: "GpuTemp");
        EnsureMeterOrderEntry(settings.General.MeterOrder, "GpuShared",    afterKey: "GpuDedicated");

        if (!Has(rawJson, "GpuDedicatedPie")) settings.Colors.GpuDedicatedPie = "#4ECDC4";
        if (!Has(rawJson, "GpuSharedPie"))    settings.Colors.GpuSharedPie    = "#A5D6A7";

        if (!Has(rawJson, "ShowTime"))  settings.Visibility.ShowTime  = true;
        if (!Has(rawJson, "Time24H"))   settings.General.Time24H       = true;
        if (!Has(rawJson, "TimeText"))  settings.Colors.TimeText       = "#FFD54F";

        if (Has(rawJson, "Background") && colors.ValueKind == JsonValueKind.Object)
        {
            // JsonDocument returns null for explicit JSON null without throwing out of GetString,
            // matching the legacy TryReadString behavior for {"Background":null} — preserving the
            // pinned "BackgroundIsNull leaves Background null" test contract. The
            // colors.ValueKind == Object guard above is what makes this safe when doc is null /
            // root is not an Object — without it colors would be a default(JsonElement) whose
            // TryGetProperty throws InvalidOperationException.
            string? legacyBg = colors.TryGetProperty("Background", out var bgEl) ? bgEl.GetString() : null;
            legacyBg = legacyBg?.Trim();
            if (string.Equals(legacyBg, "#FF202020", StringComparison.OrdinalIgnoreCase))
                settings.Colors.Background = "#CC202020";
        }

        EnsureMeterOrderEntry(settings.General.MeterOrder, "Time", afterKey: null);

        if (Has(rawJson, "DockOnTaskbar") && canReadStructured)
        {
            // The legacy field lived at the *top level* of the JSON, not under Window. canReadStructured
            // gates the root.TryGetProperty inside JsonReadBool — falls back to defaultValue when the
            // document is unparseable.
            bool legacyDock = JsonReadBool(root, "DockOnTaskbar", defaultValue: true);
            settings.Window.StickToTaskbar = legacyDock;
        }
        if (Has(rawJson, "WindowMode") && canReadStructured)
        {
            string legacyMode = JsonReadString(root, "WindowMode") ?? "AppBar";
            bool legacyFloating = legacyMode.Equals("Floating", StringComparison.OrdinalIgnoreCase);
            if (!Has(rawJson, "DockOnTaskbar"))
                settings.Window.StickToTaskbar = !legacyFloating;
        }
        if (!Has(rawJson, "MonitorIndex"))
            settings.Window.MonitorIndex = 0;
        if (settings.Window.MonitorIndex < 0)
            settings.Window.MonitorIndex = 0;
    }

    /// <summary>
    /// Tolerant parse of the raw JSON document. Malformed JSON (a corrupt backup file, partial
    /// save mid-write, etc.) runs through MigrateSettings' cheap "Has(rawJson, …)" substring
    /// checks but the JsonDocument value extraction gracefully falls back to defaults instead
    /// of throwing JsonException out of <see cref="Load"/>.
    /// </summary>
    private static JsonDocument? InitializeJsonDocument(string rawJson)
    {
        try { return JsonDocument.Parse(rawJson); }
        catch (Exception ex) { WinMeters.Log.D($"AppSettings.InitializeJsonDocument: {ex.Message}"); return null; }
    }

    private static bool JsonReadBool(in JsonElement root, string key, bool defaultValue)
    {
        if (!root.TryGetProperty(key, out var el)) return defaultValue;
        bool hasTrue = el.ValueKind == JsonValueKind.True;
        bool hasFalse = el.ValueKind == JsonValueKind.False;
        if (hasTrue) return true;
        if (hasFalse) return false;
        // Be faithful to the legacy behavior: any non-bool value (string "true"/"false",
        // numeric, null) also returns true if the first detected character is t/T, false if f/F.
        string? s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (!string.IsNullOrEmpty(s))
        {
            switch (char.ToLowerInvariant(s[0]))
            {
                case 't': return true;
                case 'f': return false;
            }
            if (bool.TryParse(s, out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static string? JsonReadString(in JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null => null,
            _ => el.GetRawText() // legacy hand scanner returned whatever was between the quotes; raw text keeps parity for non-string scalars.
        };
    }

    private static void EnsureMeterOrderEntry(List<string> order, string key, string? afterKey)
    {
        if (order.Contains(key)) return;
        int insertAt = afterKey is null ? order.Count : order.IndexOf(afterKey) + 1;
        if (insertAt <= 0) insertAt = order.Count;
        order.Insert(insertAt, key);
    }

    public void Save()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                try { File.Copy(SettingsPath, BackupPath, overwrite: true); }
                catch (Exception ex) { WinMeters.Log.D($"Save: backup failed: {ex.Message}"); }
            }
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) { WinMeters.Log.D($"Save: {ex}"); }
    }

    public class GeneralSettings
    {
        public int RefreshRateMs { get; set; } = 1000;
        public double Opacity { get; set; } = 1.0;
        public double Scale { get; set; } = 1.0;
        public bool KeepOnTop { get; set; } = true;
        public bool HideInFullscreen { get; set; } = false;
        public string? NetworkInterfaceName { get; set; }
        public string DiskInstanceName { get; set; } = "_Total";
        public bool CombineLogicalCores { get; set; } = true;
        public List<string> MeterOrder { get; set; } = ["Cpu", "CpuTemp", "GpuTemp", "GpuDedicated", "GpuShared", "Ram", "Disk", "Net", "Time"];
        public bool NavRailCollapsed { get; set; } = false;
        public bool EnableHardwareMonitor { get; set; } = true;
        public bool Time24H { get; set; } = true;
        /// <summary>
        /// Global hotkey chord in <c>"[Mods+]Key"</c> form (e.g. <c>"Ctrl+Shift+M"</c>,
        /// <c>"Alt+Shift+H"</c>). Defaults to the historical hard-coded
        /// <c>"Ctrl+Alt+Shift+M"</c>. Resolved by
        /// <see cref="WinMeters.Services.HotkeyService.ParseHotkeyString"/> at register
        /// time — unrecognized multi-character tokens (e.g. <c>"F12"</c>) log a warning
        /// and fall back to <c>VK_M</c> rather than trapping the user on a silent no-op.
        /// </summary>
        public string Hotkey { get; set; } = "Ctrl+Alt+Shift+M";
    }

    public class WindowSettings
    {
        public bool LockPosition { get; set; } = false;
        public bool StickToTaskbar { get; set; } = true;
        public double? PositionX { get; set; }
        public double? PositionY { get; set; }
        public double Height { get; set; }
        public bool IsHiddenByUser { get; set; }
        public int MonitorIndex { get; set; } = 0;
    }

    public class ColorSettings
    {
        public string Accent { get; set; } = "#00CCFF";
        public string Background { get; set; } = "#CC202020";
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

}
