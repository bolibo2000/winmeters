using System.Threading;
using System.Windows;
using System.Security.Principal;
using System.Windows.Threading;

namespace WinMeters
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstanceMutex;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                // Errors in the unhandled-exception handler itself are fatal-diagnostic — use Log.E so they
                // land in the rolling error file even in Release where Debug.WriteLine is compiled out.
                Log.E(ex ?? new InvalidOperationException($"Unhandled non-Exception object: {e.ExceptionObject}"),
                    $"AppDomain.UnhandledException (isTerminating={e.IsTerminating})");
                System.Windows.MessageBox.Show(
                    $"WinMeters encountered a fatal error and will close.\n\nA log was written to %LOCALAPPDATA%\\WinMeters\\winmeters.log.\n\n{ex?.Message ?? ex?.ToString()}",
                    "WinMeters - Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception nested)
            {
                // Absolute last-ditch: never throw out of an unhandled-exception sink.
                try { Log.E(nested, "OnAppDomainUnhandledException itself threw"); } catch { }
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Log.E(e.Exception, "DispatcherUnhandledException");
                System.Windows.MessageBox.Show(
                    $"WinMeters UI error: {e.Exception.Message}\n\nA log was written to %LOCALAPPDATA%\\WinMeters\\winmeters.log.\n\nRecovered when possible.",
                    "WinMeters - UI Error", MessageBoxButton.OK, MessageBoxImage.Error);

                // Severity classification: recoverable transient UI errors shouldn't kill the
                // whole bar (a single measure/arrange fault, a counter IO failure, etc.)
                // Only hard-fault our own coordinator types or repeated fatal cascades.
                bool unrecoverable = e.Exception is OutOfMemoryException
                    or System.ComponentModel.Win32Exception
                    || (Log.ErrorCount > Constants.Process.MaxErrorsBeforeShutdown);

                e.Handled = true;
                if (unrecoverable) Shutdown();
            }
            catch (Exception nested)
            {
                try { Log.E(nested, "OnDispatcherUnhandledException itself threw"); } catch { }
                try { e.Handled = true; Shutdown(); } catch { }
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Acquire the single-instance mutex. AbandonedMutexException at construction means
            // a previous instance crashed without releasing — re-acquire the handle into our
            // field so subsequent shutdown paths still dispose it (otherwise the OS keeps one
            // pinned alive and every launch reads "already abandoned" forever).
            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(true, Constants.Process.SingleInstanceMutexName, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                try
                {
                    _singleInstanceMutex?.Dispose();
                    _singleInstanceMutex = new Mutex(true, Constants.Process.SingleInstanceMutexName, out createdNew);
                }
                catch (Exception retryEx)
                {
                    createdNew = false;
                    Log.E(retryEx, "OnStartup: failed to recover singleton mutex after AbandonedMutex");
                }
            }

            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "WinMeters is already running.\n\nCheck your system tray or taskbar for the existing instance.",
                    "WinMeters", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Initialize the OS dark-mode preference once at app startup so all subsequently
            // opened windows (MainWindow, SettingsWindow, AboutWindow) inherit it instead of
            // each having to call ThemeService inside OnSourceInitialized after the first WM
            // has already rendered once with the wrong mode.
            Services.ThemeService.InitializeDarkMode();

            if (!IsRunningAsAdmin())
                WinMeters.Log.D("App: Not running as administrator. Some performance counters may be limited.");

            var settings = AppSettings.Load();
            WinMeters.Log.D($"App starting. Settings loaded (Accent={settings.Colors.Accent}).");
        }

        public static void ReleaseSingleInstanceMutex()
        {
            // Reuse the unified disposal helper so the release/retry path and OnExit cannot
            // diverge again. Idempotent against `_singleInstanceMutex` flipping to null by the receiver.
            DisposeSingleInstanceMutex(context: "ReleaseSingleInstanceMutex");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DisposeSingleInstanceMutex(context: "OnExit");
            base.OnExit(e);
            WinMeters.Log.D("App exiting.");
        }

        private static void DisposeSingleInstanceMutex(string context)
        {
            if (_singleInstanceMutex is not { } mutex) return;
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException ex) { WinMeters.Log.D($"{context}: ReleaseMutex: {ex.Message}"); }
            catch (ObjectDisposedException) { /* already disposed on a prior call */ }
            try { mutex.Dispose(); }
            catch (Exception ex) { WinMeters.Log.D($"{context} dispose: {ex.Message}"); }
            _singleInstanceMutex = null;
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) { Log.E(ex, "IsRunningAsAdmin"); return false; }
        }
    }
}
