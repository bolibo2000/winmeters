using System;

namespace WinMeters.Services;

/// <summary>
/// Single source of truth for the SettingsWindow checkbox → AppSettings bindings.
/// Returns a flat array of <c>(string Name, Action&lt;bool&gt; Apply)</c> pairs so the
/// bindings can be unit-tested directly (see <c>Tests/SettingsBindingsTests</c>)
/// without spinning up the WPF + XAML surface \u2014 constructing a real
/// <c>SettingsWindow</c> requires an STA thread + XAML InitializeComponent call
/// which xUnit's default thread-pool runner cannot satisfy.
///
/// <para>
/// The runtime caller (<see cref="WinMeters.SettingsWindow.ApplyValuesToWorking"/>)
/// pairs each <c>Name</c> with the corresponding <c>WnControls.CheckBox</c>
/// instance via a string-keyed dictionary and pushes <c>chk.IsChecked == true</c>
/// into the captured <c>Apply</c> action. The test path bypasses the WPF
/// resolution entirely \u2014 it just iterates the array, finds the binding by name,
/// and exercises the action against a fresh <see cref="AppSettings"/>.
/// </para>
///
/// <para>
/// Wiring-contract note: each row's <c>Action&lt;bool&gt;</c> lambda must
/// mutate the specific <see cref="AppSettings"/> field listed in the row.
/// A future contributor who re-wires a binding to the wrong field (e.g.
/// changes <c>ChkLockPosition</c> from <c>config.Window.LockPosition</c>
/// to <c>config.Window.LockPosition_X</c>) will fail the corresponding
/// row in <c>Tests.SettingsBindingsTests.WiringRow_Apply_TrueFlipsCanonicalTarget</c>
/// at design-time rather than silently land in production. The current
/// <see cref="AppSettings"/> layout has each field living in exactly one
/// section class (no cross-section duplicates to drift between), so the
/// test pattern is "field on the canonical class flipped" rather than
/// "field on a duplicate class didn't flip".
/// </para>
/// </summary>
internal static class SettingsBindings
{
    /// <summary>
    /// Returns the canonical checkbox \u2192 AppSettings bindings driven by
    /// <see cref="WinMeters.SettingsWindow.ApplyValuesToWorking"/>. The order
    /// is stable (matches the SettingsWindow.xaml visual top-to-bottom), and
    /// the string <c>Name</c> matches the CheckBox's <c>x:Name</c> attribute so
    /// the dictionary lookup in the caller is just <c>dictionary[Name]</c>.
    /// </summary>
    /// <param name="config">The AppSettings instance whose properties the
    /// <c>Apply</c> actions mutate. The caller passes <c>_working</c> at
    /// runtime; tests pass a fresh <c>new AppSettings()</c> to inspect the
    /// post-application state without polluting the dialog's working copy.</param>
    /// <returns>A 15-row array of <c>(Name, Apply)</c> pairs.</returns>
    public static (string Name, Action<bool> Apply)[] GetVisibilityBindings(AppSettings config)
    {
        return new (string Name, Action<bool> Apply)[]
        {
            ("ChkCpu",              v => config.Visibility.ShowCpu = v),
            ("ChkRam",              v => config.Visibility.ShowRam = v),
            ("ChkDisk",             v => config.Visibility.ShowDisk = v),
            ("ChkNet",              v => config.Visibility.ShowNet = v),
            ("ChkCpuTemp",          v => config.Visibility.ShowCpuTemp = v),
            ("ChkGpuTemp",          v => config.Visibility.ShowGpuTemp = v),
            ("ChkGpuDedicated",     v => config.Visibility.ShowGpuDedicated = v),
            ("ChkGpuShared",        v => config.Visibility.ShowGpuShared = v),
            ("ChkCombineCpu",       v => config.General.CombineLogicalCores = v),
            ("ChkTime",             v => config.Visibility.ShowTime = v),
            ("ChkTime24H",          v => config.General.Time24H = v),
            ("ChkLockPosition",     v => config.Window.LockPosition = v),
            ("ChkHideInFullscreen", v => config.General.HideInFullscreen = v),
            ("ChkSnapToTaskbar",    v => config.Window.StickToTaskbar = v),
            // ChkKeepOnTop binds to GeneralSettings.KeepOnTop (the runtime toggle
            // in MainWindow.ToggleKeepOnTop reads from this home). Wiring-contract
            // test SettingsBindingsTests.WiringRow_Apply_TrueFlipsCanonicalTarget
            // pins this row to config.General.KeepOnTop.
            ("ChkKeepOnTop",        v => config.General.KeepOnTop = v),
        };
    }

    /// <summary>
    /// All binding names exposed by <see cref="GetVisibilityBindings"/>, in
    /// the same order. Derived once at type-init from the lambda list so
    /// adding a new row to <see cref="GetVisibilityBindings"/> automatically
    /// extends this list (no third-place hand-sync). Consumers:
    /// <list type="bullet">
    /// <item><description><c>Tests.SettingsBindingsTests.AllBindingNames_CountMatchesGetVisibilityBindings</c>
    ///   locks the count agreement between this list and the live method.</description></item>
    /// <item><description><c>Tests.SettingsBindingsTests.AllBindingNames_SetMatchesTestSideWiringRowsNames</c>
    ///   locks the cross-check against the test-side <c>WiringRows</c> name set.</description></item>
    /// <item><description><c>WinMeters.SettingsWindow.ApplyValuesToWorking</c>'s
    ///   <c>Debug.Assert</c> confirms its <c>chkByName</c> dictionary keys
    ///   SetEquals this list at design-time so missing WPF CheckBox x:Name
    ///   attributes are caught before the runtime KeyNotFoundException.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> AllBindingNames { get; } = BuildAllBindingNames();

    private static string[] BuildAllBindingNames()
    {
        var bindings = GetVisibilityBindings(new AppSettings());
        var names = new string[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            names[i] = bindings[i].Name;
        return names;
    }
}
