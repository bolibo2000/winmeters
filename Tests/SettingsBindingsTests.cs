using System;
using System.Collections.Generic;
using System.Linq;
using WinMeters.Services;
using Xunit;

namespace WinMeters.Tests;

/// <summary>
/// Wiring-contract tests for <see cref="SettingsBindings.GetVisibilityBindings"/>.
///
/// <para>
/// Removing either theory weakens the suite \u2014
/// <c>WiringRow_ApplyTrueFlipsCanonicalTarget</c> catches "lambda writes
/// the wrong field"; <c>WiringRow_ApplyFalseFlipsBack</c> is currently the
/// only test catching "lambda ignores input value and always writes the
/// same bool" regressions. Adding a second theory that catches that
/// regression class wouldn't invalidate this doc-comment's claim because
/// the wording is "currently the only" rather than "the only".
/// </para>
///
/// <para>
/// The actual AppSettings layout has each field living in exactly one
/// section class (no cross-section duplicates to drift between), so the
/// test pattern is "canonical field flipped after apply(...)" rather
/// than "duplicate field did not flip" \u2014 keep this assumption in mind
/// when adding new bindings.
/// </para>
/// </summary>
public class SettingsBindingsTests
{
    /// <summary>
    /// Per-binding wiring spec returned as <c>IEnumerable&lt;object[]&gt;</c>
    /// (the xUnit 2.9.2 [MemberData] shape \u2014 typed-tuple IEnumerable is
    /// flagged xUnit1019 by the analyzer). The <see cref="Row"/> helper
    /// hoists the boxing + Func/Action cast into one place; each yield below
    /// is one readable line passing the name + setter + getter through.
    /// </summary>
    public static IEnumerable<object[]> WiringRows()
    {
        yield return Row("ChkCpu",              (s, v) => s.Visibility.ShowCpu = v,          s => s.Visibility.ShowCpu);
        yield return Row("ChkRam",              (s, v) => s.Visibility.ShowRam = v,          s => s.Visibility.ShowRam);
        yield return Row("ChkDisk",             (s, v) => s.Visibility.ShowDisk = v,         s => s.Visibility.ShowDisk);
        yield return Row("ChkNet",              (s, v) => s.Visibility.ShowNet = v,          s => s.Visibility.ShowNet);
        yield return Row("ChkCpuTemp",          (s, v) => s.Visibility.ShowCpuTemp = v,      s => s.Visibility.ShowCpuTemp);
        yield return Row("ChkGpuTemp",          (s, v) => s.Visibility.ShowGpuTemp = v,      s => s.Visibility.ShowGpuTemp);
        yield return Row("ChkGpuDedicated",     (s, v) => s.Visibility.ShowGpuDedicated = v, s => s.Visibility.ShowGpuDedicated);
        yield return Row("ChkGpuShared",        (s, v) => s.Visibility.ShowGpuShared = v,    s => s.Visibility.ShowGpuShared);
        yield return Row("ChkCombineCpu",       (s, v) => s.General.CombineLogicalCores = v,  s => s.General.CombineLogicalCores);
        yield return Row("ChkTime",             (s, v) => s.Visibility.ShowTime = v,         s => s.Visibility.ShowTime);
        yield return Row("ChkTime24H",          (s, v) => s.General.Time24H = v,             s => s.General.Time24H);
        yield return Row("ChkLockPosition",     (s, v) => s.Window.LockPosition = v,         s => s.Window.LockPosition);
        yield return Row("ChkHideInFullscreen", (s, v) => s.General.HideInFullscreen = v,    s => s.General.HideInFullscreen);
        yield return Row("ChkSnapToTaskbar",    (s, v) => s.Window.StickToTaskbar = v,       s => s.Window.StickToTaskbar);
        yield return Row("ChkKeepOnTop",        (s, v) => s.General.KeepOnTop = v,           s => s.General.KeepOnTop);
    }

    private static object[] Row(string name, Action<AppSettings, bool> set, Func<AppSettings, bool> get)
        => new object[] { name, set, get };

    [Theory]
    [MemberData(nameof(WiringRows))]
    public void WiringRow_ApplyTrueFlipsCanonicalTarget(
        string checkBoxName,
        Action<AppSettings, bool> canonicalSetter,
        Func<AppSettings, bool> canonicalGetter)
    {
        var settings = new AppSettings();
        canonicalSetter(settings, false);

        var binding = SettingsBindings.GetVisibilityBindings(settings)
            .First(b => b.Name == checkBoxName);

        binding.Apply(true);

        Assert.True(canonicalGetter(settings),
            $"Binding for '{checkBoxName}' did not flip its canonical AppSettings field to true \u2014 " +
            $"the lambda mutated the wrong field, or the property path is broken.");
    }

    [Theory]
    [MemberData(nameof(WiringRows))]
    public void WiringRow_ApplyFalseFlipsBack(
        string checkBoxName,
        Action<AppSettings, bool> canonicalSetter,
        Func<AppSettings, bool> canonicalGetter)
    {
        var settings = new AppSettings();
        canonicalSetter(settings, true);

        var binding = SettingsBindings.GetVisibilityBindings(settings)
            .First(b => b.Name == checkBoxName);

        binding.Apply(false);

        Assert.False(canonicalGetter(settings),
            $"Binding for '{checkBoxName}' did not flip its canonical AppSettings field back to false " +
            $"on apply(false) \u2014 the lambda ignored the input value.");
    }

    [Fact]
    public void SettingsBindings_ReturnsFifteenRows()
    {
        var bindings = SettingsBindings.GetVisibilityBindings(new AppSettings());
        Assert.Equal(15, bindings.Length);
    }

    [Fact]
    public void SettingsBindings_AllNamesAreUniqueAndMatchExpected()
    {
        var names = SettingsBindings.GetVisibilityBindings(new AppSettings())
            .Select(b => b.Name)
            .ToList();

        Assert.Equal(15, names.Distinct().Count());

        var expected = new HashSet<string>
        {
            "ChkCpu", "ChkRam", "ChkDisk", "ChkNet",
            "ChkCpuTemp", "ChkGpuTemp", "ChkGpuDedicated", "ChkGpuShared",
            "ChkCombineCpu", "ChkTime", "ChkTime24H",
            "ChkLockPosition", "ChkHideInFullscreen", "ChkSnapToTaskbar", "ChkKeepOnTop",
        };
        Assert.Equal(expected, names.ToHashSet());
    }

    [Fact]
    public void AllBindingNames_CountMatchesGetVisibilityBindings()
    {
        // Locks the count agreement between SettingsBindings.AllBindingNames
        // (cached at type-init, derived from GetVisibilityBindings once) and
        // the live GetVisibilityBindings array. If a future contributor adds
        // GetVisibilityBindings row without re-touching the AllBindingNames
        // cache flow, the count mismatch surfaces here at test time.
        Assert.Equal(
            SettingsBindings.AllBindingNames.Count,
            SettingsBindings.GetVisibilityBindings(new AppSettings()).Length);
    }

    [Fact]
    public void AllBindingNames_SetMatchesTestSideWiringRowsNames()
    {
        // Locks the test-side WiringRows name-set against AllBindingNames.
        // If a contributor adds a row to SettingsBindings.GetVisibilityBindings
        // but forgets to add a matching WiringRows row, this fails; if they
        // add a WiringRows row but back it with no real binding (typo in the
        // name), this also fails.
        var bindingNames = SettingsBindings.AllBindingNames.ToHashSet();
        var testSideNames = new HashSet<string>();
        foreach (var row in WiringRows())
            testSideNames.Add((string)row[0]);
        Assert.Equal(bindingNames, testSideNames);
    }
}
