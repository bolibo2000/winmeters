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
        // Migrate legacy/old MeterOrder keys (with spaces or different casings) to standard keys
        for (int i = 0; i < settings.General.MeterOrder.Count; i++)
        {
            var key = settings.General.MeterOrder[i];
            var newKey = key switch
            {
                "Cpu Temp" => "CpuTemp",
                "Gpu Temp" => "GpuTemp",
                "VRAM Pie" => "GpuDedicated",
                "SRAM Pie" => "GpuShared",
                "Ram Pie" => "Ram",
                "Disk Pie" => "Disk",
                _ => key
            };
            settings.General.MeterOrder[i] = newKey;
        }

        // Deduplicate the list to ensure clean settings
        var distinctOrder = settings.General.MeterOrder.Distinct().ToList();
        settings.General.MeterOrder = distinctOrder;

        if (!rawJson.Contains("ShowGpuDedicated", StringComparison.OrdinalIgnoreCase))
            settings.Visibility.ShowGpuDedicated = true;
        if (!rawJson.Contains("ShowGpuShared", StringComparison.OrdinalIgnoreCase))
            settings.Visibility.ShowGpuShared = true;

        if (!settings.General.MeterOrder.Contains("GpuDedicated"))
        {
            int insertAt = settings.General.MeterOrder.IndexOf("GpuTemp") is var idx and >= 0 ? idx + 1 : settings.General.MeterOrder.Count;
            settings.General.MeterOrder.Insert(insertAt, "GpuDedicated");
        }
        if (!settings.General.MeterOrder.Contains("GpuShared"))
        {
            int insertAt = settings.General.MeterOrder.IndexOf("GpuDedicated") is var idx and >= 0 ? idx + 1 : settings.General.MeterOrder.Count;
            settings.General.MeterOrder.Insert(insertAt, "GpuShared");
        }

        if (!rawJson.Contains("GpuDedicatedPie", StringComparison.OrdinalIgnoreCase))
            settings.Colors.GpuDedicatedPie = "#4ECDC4";
        if (!rawJson.Contains("GpuSharedPie", StringComparison.OrdinalIgnoreCase))
            settings.Colors.GpuSharedPie = "#A5D6A7";

        if (!rawJson.Contains("ShowTime", StringComparison.OrdinalIgnoreCase))
            settings.Visibility.ShowTime = true;
        if (!rawJson.Contains("Time24H", StringComparison.OrdinalIgnoreCase))
            settings.General.Time24H = true;
        if (!rawJson.Contains("TimeText", StringComparison.OrdinalIgnoreCase))
            settings.Colors.TimeText = "#FFD54F";

        if (!settings.General.MeterOrder.Contains("Time"))
        {
            settings.General.MeterOrder.Add("Time");
        }
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
        public bool StartWithWindows { get; set; } = false;
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
        public bool DockOnTaskbar { get; set; } = false;
        public double? PositionX { get; set; }
        public double? PositionY { get; set; }
        public double Height { get; set; }
        public bool IsHiddenByUser { get; set; }
    }

    public class ColorSettings
    {
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
}
