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
            // Release the mutex
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                }
                catch { }
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
