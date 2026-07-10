using System.Windows;
using System.Diagnostics;
using System.Reflection;

namespace WinMeters;

/// <summary>
/// About dialog for the WinMeters bar. Opens from the bar's RMB popup
/// "About" entry (cmd IDM_ABOUT). Mirrors the Windows native About-dialog
/// idiom: brand wordmark, brief description, version, credits, OK button.
/// Distinct from SettingsWindow (which is the per-meter / color / rate
/// configuration surface) -- About is read-only and never asks the user
/// to make any choice.
///
/// Modeless Show() so the user can keep interacting with the bar while
/// the About dialog is on screen. Single-instance gate lives on the
/// MainWindow side (<c>_existingAboutWindow</c> field); AboutWindow
/// itself is dumb -- it pops up, presents its content, and closes on
/// BtnOk_Click.
///
/// Dark-chrome parity with SettingsWindow: ThemeBgBrush / ThemeTextBrush
/// from the merged Themes/WinMetersTheme.xaml dictionary (via
/// <c>ColorHelper.ThemeBrush</c>); explicit RootGrid Background so WPF's
/// Window template can't mask it; DWM-attribute
/// DWMWA_USE_IMMERSIVE_DARK_MODE on the title bar so the OS-drawn
/// non-client area matches the WPF content area; PreferredAppMode
/// (FORCE_DARK) opt-in via <c>Services.ThemeService.InitializeDarkMode</c>
/// so the bar's RMB popup HMENU stays dark while this dialog is open.
///
/// Lifetime: 1 ctor call per OpenAboutWindow invocation. No
/// subscriptions to retain, no debounce timers, no per-control handler
/// tracking -- the dialog has only one button and one click handler.
/// Esc/Alt-F4 close the dialog via Window's default IsCancel-style
/// shortcut; both routes end at SettingsWindow_Closed equivalent
/// (just Close()), which is a no-op for state.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Opt this process into dark mode so the OS-painted chrome (the
        // bar's RMB popup HMENU; the title bar's DWMWA_USE_IMMERSIVE_DARK_MODE
        // attribute) lands on the dark variant. Win10 1903+'s per-process
        // uxtheme-aware PreferredAppMode translation only honours the dark
        // value when PreferredAppMode is set to FORCE_DARK first; mirrors
        // the same call in SettingsWindow.ctor and
        // MainWindow.OnSourceInitialized. See Services.ThemeService for
        // the Win10 1903 quirk that drives this.
        Services.ThemeService.InitializeDarkMode();

        // Background and foreground track the merged
        // Themes/WinMetersTheme.xaml dictionary (ThemeBgBrush /
        // ThemeTextBrush via ColorHelper.ThemeBrush) -- the Maximal
        // recode retired the COLOR_MENU / COLOR_MENUTEXT live-sampling
        // path in favour of a single source of truth in the theme.
        // The ?? Brushes.White fallback on this.Foreground is null-safe:
        // a resource-lookup miss for ThemeTextBrush would clear the
        // local Foreground DP and fall through to WPF's default
        // SystemColors.WindowText (black on Windows), so the fallback
        // guarantees white text regardless. RootGrid.Background
        // explicit to defend against WPF Window template masking the
        // dialog's visible client area.
        var menuBackground = ColorHelper.ThemeBrush("ThemeBgBrush");
        this.Background = menuBackground;
        RootGrid.Background = menuBackground;
        this.Foreground = ColorHelper.ThemeBrush("ThemeTextBrush") ?? System.Windows.Media.Brushes.White;

        // Fill the dynamic fields. TxtRuntime via the assembly's TargetFrameworkAttribute so the
        // displayed runtime stays in lockstep with WinMeters.csproj's
        // <TargetFramework> -- one source of truth (the csproj), not a
        // string the AboutWindow can silently drift from. The attribute
        // is always present on .NET-compiled assemblies; no fallback.
        // TxtVersion wiring is currently intentionally inactive: the
        // <TextBlock x:Name="TxtVersion"> line in AboutWindow.xaml is
        // commented out, and the displayed "Version: 2.5" label is a
        // hardcoded literal. To make the displayed version dynamically
        // follow Assembly.GetName().Version, uncomment BOTH the XAML
        // field AND this assignment -- and add an explicit <Version>
        // to WinMeters.csproj so the value is meaningful (otherwise the
        // displayed version collapses to a default '1.0.0.0').
        // TxtVersion.Text = TryGetVersion();
        TxtRuntime.Text = typeof(AboutWindow).Assembly
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()
            ?.FrameworkDisplayName ?? "net10.0-windows";

        // Credits row is informational only; no real WinMeters project
        // repo URL exists in the codebase, so no click handler is wired
        // -- the user can copy the value out of the dialog manually if
        // they want to look up the upstream. Cursor stays as the default
        // arrow so it doesn't read as a hyperlink we can't fulfil.
    }

    /// <summary>
    /// Reads <see cref="Assembly.GetEntryAssembly"/>'s Version. Returns
    /// "unknown" if the assembly metadata is missing -- which can happen
    /// in single-file publish without an explicit <c>&lt;Version&gt;</c>
    /// in the .csproj. The fallback path uses Environment.ProcessPath +
    /// FileVersionInfo to surface the Win32 VERSIONINFO block; if both
    /// fail we return "unknown" so the About dialog still opens instead
    /// of crashing the bar's RMB handler.
    /// </summary>
    private static string TryGetVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly();
            var ver = asm?.GetName().Version;
            if (ver is not null)
            {
                return ver.ToString();
            }
        }
        catch
        {
            // fall through to FileVersionInfo
        }

        try
        {
            var path = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(fvi.FileVersion))
                {
                    return fvi.FileVersion;
                }
                if (!string.IsNullOrEmpty(fvi.ProductVersion))
                {
                    return fvi.ProductVersion;
                }
            }
        }
        catch
        {
            // fall through to literal "unknown"
        }

        return "unknown";
    }

    /// <summary>OK button handler -- just closes the dialog. Esc/Alt-F4 also
    /// close via Window's built-in IsCancel-style shortcut (no handler
    /// needed -- WPF routes both to Close()).</summary>
    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Opt the dialog's HWND into the modern dark-chrome title bar so
    /// the OS-drawn non-client area matches the WPF content area's
    /// follow-OS-theme brush. Mirrors the same call in
    /// SettingsWindow.OnSourceInitialized. Distinct from uxtheme's
    /// SetPreferredAppMode(FORCE_DARK) used by the bar's RMB popup to
    /// force-dark an HMENU: this is the DWM-attribute path for
    /// title-bar darkness, available since Windows 10 1903. Best-effort:
    /// if the DWM call fails (older Windows), WinMeters.Log.D captures
    /// the HRESULT and the dialog opens with whatever default chrome the
    /// older OS gives, instead of crashing the Show().
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            int hr = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref useDark,
                sizeof(int));

            if (hr != 0)
            {
                WinMeters.Log.D($"AboutWindow.OnSourceInitialized: DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE) returned HRESULT 0x{hr:X8}.");
            }
        }
        catch (Exception ex)
        {
            WinMeters.Log.D($"AboutWindow.OnSourceInitialized: {ex.Message}");
        }
    }
}
