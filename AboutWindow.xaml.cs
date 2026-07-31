using System.Reflection;
using System.Windows;

namespace WinMeters;

public partial class AboutWindow : Window
{
    // Derived lazily from AppSettings so this constant stays in lockstep with
    // the real default even if a future contributor renames the chord string.
    // One-shot allocation on first call; the result is cached for the app lifetime.
    private static readonly string DefaultHotkeyFallback = new AppSettings().General.Hotkey;

    public AboutWindow(string? hotkey = null)
    {
        InitializeComponent();

        var bg = ColorHelper.ThemeBrush("ThemeBgBrush");
        this.Background = bg;
        RootGrid.Background = bg;
        this.Foreground = ColorHelper.ThemeBrush("ThemeTextBrush") ?? System.Windows.Media.Brushes.White;

        TxtRuntime.Text = typeof(AboutWindow).Assembly
            .GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()
            ?.FrameworkDisplayName ?? "net10.0-windows";

        TxtVersion.Text = "v" + (typeof(AboutWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "?.?.?");

        // Hotkey hint: bound from caller so it always reflects the user's saved
        // choice, never a hardcoded stale default. When the user explicitly clears
        // the hotkey to empty in Settings, don't silently reassert a chord they
        // turned off — say so and show the AppSettings-derived fallback as a hint
        // of what a freshly-installed copy would use.
        RenderHotkeyText(hotkey);
    }

    /// <summary>
    /// Pushes a new hotkey value into the live About window's hint row. Called
    /// by <c>MainWindow.ApplySettings[Live]</c> whenever the user changes the
    /// chord in Settings while the About dialog is already open — without this,
    /// the hint text was frozen at ctor time and would only update on next
    /// reopen. Uses the same whitespace-guard + default-fallback logic as the
    /// ctor so the displayed string never drifts.
    /// </summary>
    public void SetHotkey(string? hotkey) => RenderHotkeyText(hotkey);

    private void RenderHotkeyText(string? hotkey)
    {
        // WPF requires FrameworkElement property mutations on the dispatcher thread.
        // Today the only caller is MainWindow which is itself on the UI thread, but
        // a future contributor wiring this from a background source would silently
        // throw inside any outer try/catch. Marshal defensively if needed.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => RenderHotkeyText(hotkey));
            return;
        }

        TxtHotkey.Text = string.IsNullOrWhiteSpace(hotkey)
            ? $"No hotkey set (default is {DefaultHotkeyFallback})"
            : $"*Use {hotkey.Trim()} to hide/show interface";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch (Exception ex) { WinMeters.Log.D($"AboutWindow.OnSourceInitialized: {ex.Message}"); }
    }
}
