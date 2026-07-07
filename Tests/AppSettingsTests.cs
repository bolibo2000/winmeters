using System.Text.Json;
using Xunit;

namespace WinMeters.Tests;

/// <summary>
/// Covers <see cref="AppSettings"/> migration: legacy key remapping, missing-field
/// defaults, and <c>EnsureMeterOrderEntry</c> insertion order.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void MigrateSettings_RenamesLegacyMeterOrderKeys()
    {
        var settings = new AppSettings
        {
            General = new AppSettings.GeneralSettings
            {
                MeterOrder = new List<string> { "Cpu Temp", "Cpu", "VRAM Pie", "Gpu Temp", "SRAM Pie", "Ram Pie", "Disk Pie", "Net", "Time" }
            }
        };
        string raw = JsonSerializer.Serialize(settings);

        InvokeMigrate(settings, raw);

        Assert.DoesNotContain("Cpu Temp", settings.General.MeterOrder);
        Assert.DoesNotContain("Gpu Temp", settings.General.MeterOrder);
        Assert.DoesNotContain("VRAM Pie", settings.General.MeterOrder);
        Assert.DoesNotContain("SRAM Pie", settings.General.MeterOrder);
        Assert.DoesNotContain("Ram Pie", settings.General.MeterOrder);
        Assert.DoesNotContain("Disk Pie", settings.General.MeterOrder);

        Assert.Contains("CpuTemp", settings.General.MeterOrder);
        Assert.Contains("GpuTemp", settings.General.MeterOrder);
        Assert.Contains("GpuDedicated", settings.General.MeterOrder);
        Assert.Contains("GpuShared", settings.General.MeterOrder);
        Assert.Contains("Ram", settings.General.MeterOrder);
        Assert.Contains("Disk", settings.General.MeterOrder);
    }

    [Fact]
    public void MigrateSettings_DeduplicatesMeterOrder()
    {
        // Pre-stock the meter list with every key that <c>EnsureMeterOrderEntry</c> would
        // add on a fresh install (GpuTemp, GpuDedicated, GpuShared, Time). That isolates
        // dedup behaviour from the EnsureMeterOrderEntry path, which other tests in this
        // class already cover (Insert*After*, Append*WhenMissing). After dedup the list
        // should collapse from 7 items to 6 with each surviving key appearing exactly once.
        var settings = new AppSettings
        {
            General = new AppSettings.GeneralSettings
            {
                MeterOrder = new List<string>
                {
                    "Cpu", "Cpu", "Cpu", "Ram", "GpuTemp", "GpuDedicated", "GpuShared", "Time"
                }
            }
        };
        InvokeMigrate(settings, JsonSerializer.Serialize(settings));

        // Post-migration count: three "Cpu" duplicates removed → 6.
        Assert.Equal(6, settings.General.MeterOrder.Count);

        // Dedup is what this test actually exercises — assert per-key uniqueness rather
        // than a brittle positional expectation.
        var uniques = settings.General.MeterOrder.Distinct().ToList();
        Assert.Equal(settings.General.MeterOrder.Count, uniques.Count);

        // EnsureMeterOrderEntry didn't reorder anything — the original insertion order
        // (minus dupes) is preserved.
        Assert.Equal("Cpu", settings.General.MeterOrder[0]);
        Assert.Contains("Ram", settings.General.MeterOrder);
        Assert.Contains("GpuTemp", settings.General.MeterOrder);
        Assert.Contains("GpuDedicated", settings.General.MeterOrder);
        Assert.Contains("GpuShared", settings.General.MeterOrder);
        Assert.Contains("Time", settings.General.MeterOrder);
    }

    [Fact]
    public void MigrateSettings_AppliesMissingFieldDefaults()
    {
        // A minimal JSON that lacks every "newer" field token.
        var settings = new AppSettings();
        const string raw = "{\"General\":{},\"Window\":{},\"Colors\":{},\"Visibility\":{},\"Rates\":{}}";

        InvokeMigrate(settings, raw);

        Assert.True(settings.Visibility.ShowGpuDedicated);
        Assert.True(settings.Visibility.ShowGpuShared);
        Assert.True(settings.Visibility.ShowTime);
        Assert.Equal("#4ECDC4", settings.Colors.GpuDedicatedPie);
        Assert.Equal("#A5D6A7", settings.Colors.GpuSharedPie);
        Assert.True(settings.General.Time24H);
        Assert.Equal("#FFD54F", settings.Colors.TimeText);
    }

    [Fact]
    public void MigrateSettings_PreservesExplicitValuesWhenFieldIsPresent()
    {
        var raw = "{\"Visibility\":{\"ShowGpuDedicated\":false,\"ShowCpu\":false,\"ShowTime\":false},\"Colors\":{\"GpuDedicatedPie\":\"#000000\"}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        Assert.False(settings.Visibility.ShowGpuDedicated); // explicit, not overridden
        Assert.False(settings.Visibility.ShowCpu);
        Assert.False(settings.Visibility.ShowTime);
        Assert.Equal("#000000", settings.Colors.GpuDedicatedPie);
    }

    [Fact]
    public void MigrateSettings_InsertsGpuDedicatedAfterGpuTemp()
    {
        var settings = new AppSettings
        {
            General = new AppSettings.GeneralSettings
            {
                MeterOrder = new List<string> { "Cpu", "CpuTemp", "GpuTemp", "Ram", "Net" }
            }
        };
        InvokeMigrate(settings, JsonSerializer.Serialize(settings));

        int idxGpuTemp = settings.General.MeterOrder.IndexOf("GpuTemp");
        int idxGpuDedicated = settings.General.MeterOrder.IndexOf("GpuDedicated");
        Assert.True(idxGpuDedicated == idxGpuTemp + 1, $"GpuDedicated should sit right after GpuTemp; got GpuTemp={idxGpuTemp}, GpuDedicated={idxGpuDedicated}");
    }

    [Fact]
    public void MigrateSettings_AppendsGpuSharedAfterGpuDedicated()
    {
        var settings = new AppSettings
        {
            General = new AppSettings.GeneralSettings
            {
                MeterOrder = new List<string> { "Cpu", "Ram", "Disk" }
            }
        };
        InvokeMigrate(settings, JsonSerializer.Serialize(settings));

        int idxGpuDedicated = settings.General.MeterOrder.IndexOf("GpuDedicated");
        int idxGpuShared = settings.General.MeterOrder.IndexOf("GpuShared");
        Assert.True(idxGpuShared == idxGpuDedicated + 1, "GpuShared should sit right after GpuDedicated");
    }

    [Fact]
    public void MigrateSettings_AppendsTimeWhenMissing()
    {
        var settings = new AppSettings
        {
            General = new AppSettings.GeneralSettings
            {
                MeterOrder = new List<string> { "Cpu", "Ram" }
            }
        };
        InvokeMigrate(settings, JsonSerializer.Serialize(settings));

        Assert.Contains("Time", settings.General.MeterOrder);
        Assert.Equal("Time", settings.General.MeterOrder[^1]);
    }

    [Fact]
    public void MigrateSettings_RebasesLegacyBackgroundDefault()
    {
        // Built off the AppliesMissingFieldDefaults JSON shape so the
        // only "present" Color field is the legacy Background ->
        // exercises the rebase guard without hauling in unrelated
        // Colors defaults. Pre-recode JSON had "Background":"#FF202020"
        // as the property initializer's default; a legacy user with
        // that value in their settings.json file should land on the
        // new translucent "#CC202020" on the next load.
        const string raw = "{\"Colors\":{\"Background\":\"#FF202020\"}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        Assert.Equal("#CC202020", settings.Colors.Background);
    }

    [Fact]
    public void MigrateSettings_PreservesCustomBackgroundWhenNotLegacyDefault()
    {
        // User picked opaque black #000000FF — clearly not the legacy
        // default, the migration rule's case-insensitive equality
        // gate must leave it untouched. The whole point of the gate
        // is to avoid clobbering users who picked their own hex.
        const string raw = "{\"Colors\":{\"Background\":\"#000000FF\"}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        Assert.Equal("#000000FF", settings.Colors.Background);
    }

    [Fact]
    public void MigrateSettings_RebasesLegacyBackgroundDefaultCaseInsensitive()
    {
        // Hand-edited JSON with lowercase hex — rare but possible if
        // a user opens settings.json in Notepad, types their own
        // value, then drops back. The case-insensitive gate covers
        // this so the rebase succeeds for hand-edited files alongside
        // the JsonSerializer-emitted canonical upper case.
        const string raw = "{\"Colors\":{\"Background\":\"#ff202020\"}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        Assert.Equal("#CC202020", settings.Colors.Background);
    }

    [Fact]
    public void MigrateSettings_AppliesNewBackgroundDefaultWhenAbsent()
    {
        // A minimal JSON that omits the Colors.Background key. The
        // property initializer on ColorSettings fills it with
        // "#CC202020" on construction; MigrateSettings does not touch
        // the value because Has(rawJson, "Background") returns false.
        // This pins the property-initializer fallback so a future
        // refactor that flips the default back to "#FF202020" (or
        // inverts the absent-vs-present detection) fails loudly
        // rather than regressing first-launch users silently.
        const string raw = "{\"Colors\":{}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        Assert.Equal("#CC202020", settings.Colors.Background);
    }

    /// <summary>
    /// Invokes the private <c>AppSettings.MigrateSettings</c> via reflection. The method
    /// takes a raw JSON token so we control which fields appear "present" vs missing.
    /// </summary>
    private static void InvokeMigrate(AppSettings settings, string rawJson)
    {
        var method = typeof(AppSettings).GetMethod(
            "MigrateSettings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("MigrateSettings not found");
        method.Invoke(null, new object[] { settings, rawJson });
    }
}
