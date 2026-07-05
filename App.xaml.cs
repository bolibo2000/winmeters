using System.Threading;
using System.Windows;
using System.Security.Principal;

namespace WinMeters
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Theme is built in plain WPF: Kil0bitTheme.xaml merged globally by
            // App.xaml. We intentionally do NOT depend on Wpf.Ui (its 3.x line
            // only ships net451 assemblies; cannot be used on net10.0-windows).
            // Re-evaluated periodically: when Wpf.Ui publishes a real
            // net8.0/net10.0 build we can switch the ToggleSwitch to a native
            // Fluent control without touching the rest of the dialog.

            // Single-instance enforcement
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, Constants.Process.SingleInstanceMutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance is already running
                System.Windows.MessageBox.Show(
                    "WinMeters is already running.\n\nCheck your system tray or taskbar for the existing instance.",
                    "WinMeters",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Shutdown();
                return;
            }

            // Check for admin privileges (some performance counters may need it)
            if (!IsRunningAsAdmin())
            {
                WinMeters.Log.D("App: Not running as administrator. Some performance counters may be limited.");
            }

            // Load settings (with backup/restore support)
            var settings = AppSettings.Load();
            WinMeters.Log.D("App starting. Settings loaded.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Release the mutex. ReleaseMutex throws ApplicationException if the current
            // thread doesn't own it (e.g., shutdown after the startup pre-empt path), so
            // we wrap-and-log rather than swallow.
            if (_singleInstanceMutex is { } mutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    WinMeters.Log.D($"App.OnExit: ReleaseMutex failed: {ex.Message}");
                }

                try { mutex.Dispose(); }
                catch (Exception ex) { WinMeters.Log.D($"App.OnExit: dispose failed: {ex.Message}"); }
            }

            base.OnExit(e);
            WinMeters.Log.D("App exiting.");
        }

        /// <summary>
        /// Checks if the application is running with administrator privileges.
        /// </summary>
        private static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
