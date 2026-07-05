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
            // Surface unhandled exceptions instead of dying silently. The user
            // reported 'WinMeters does not start, not showing in Task Manager'
            // which usually means an exception fired before MainWindow.Show()
            // and the OS already-terminated process is invisible. Showing the
            // crash message + log path turns that into something actionable.
            // Handlers are wired in App() (before StartupUri fires) so they
            // catch failures during InitializeComponent as well.
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        /// <summary>
        /// AppDomain-level crash sink. Fires on any unhandled exception on
        /// any thread (background, threadpool, DispatcherTimer). We log via
        /// WinMeters.Log and surface a MessageBox so the user has evidence
        /// even if the process is killed immediately afterwards by the OS.
        /// </summary>
        private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                WinMeters.Log.D($"AppDomain.UnhandledException (isTerminating={e.IsTerminating}): {ex}");
                System.Windows.MessageBox.Show(
                    $"WinMeters encountered a fatal error and will close.\n\n{ex?.Message ?? ex?.ToString()}",
                    "WinMeters - Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Last-ditch; we are already in a fatal path. Swallow so the
                // default unhandled-exception machinery can still terminate
                // the process cleanly.
            }
        }

        /// <summary>
        /// WPF Dispatcher (UI thread) crash sink. Marks the exception as
        /// Handled so the application can shut down gracefully via the next
        /// Shutdown() call rather than Windows terminating it abruptly.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                WinMeters.Log.D($"DispatcherUnhandledException: {e.Exception}");
                System.Windows.MessageBox.Show(
                    $"WinMeters UI error: {e.Exception.Message}\n\nThe error has been logged.",
                    "WinMeters - UI Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                e.Handled = true;
                // Treat a UI-thread exception as fatal so we don't keep
                // pumping dispatch ops on a half-broken UI thread. OnExit
                // fires and releases the single-instance mutex cleanly.
                Shutdown();
            }
            catch
            {
                // fall through; the default handler terminates the process.
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single-instance enforcement FIRST so we do not spin up MainWindow
            // (and its hardware-monitor thread + WPF DPI dance) just to discover
            // another WinMeters.exe is already running. If a previous instance
            // crashed via kill -9 -- common in fullscreen games where the
            // foreground process can yank us out by force -- the mutex ends
            // up ABANDONED; the .NET BCL throws AbandonedMutexException on
            // the next acquire, which we recover from so the user is not
            // stuck unable to launch a fresh instance.
            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(
                    true, Constants.Process.SingleInstanceMutexName, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner crashed without releasing the mutex.
                // The BCL's `new Mutex` ctor quietly grants the calling
                // thread ownership of the kernel handle before throwing
                // AbandonedMutexException as a signal, but the C# Mutex
                // wrapper itself was never assigned. The `createdNew` out
                // param is unreliable (typically false) on this throw
                // path. We are effectively the new owner; set createdNew
                // = true so the "already running" branch does NOT fire.
                // OnExit null-checks `_singleInstanceMutex` -- a missing
                // wrapper just skips ReleaseMutex. The OS auto-releases any
                // owned handle when the process exits, so the abandoned
                // mutex is cleaned up next boot.
                createdNew = true;
            }

            if (!createdNew)
            {
                // Another healthy instance is already running.
                System.Windows.MessageBox.Show(
                    "WinMeters is already running.\n\nCheck your system tray or taskbar for the existing instance.",
                    "WinMeters",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Shutdown();
                return;
            }

            // Safe to start the WPF machinery now (MainWindow +
            // InitializeComponent + ResourceDictionary merges via
            // App.xaml). base.OnStartup fires the StartupUri which constructs
            // MainWindow; we want that only after we know we're the unique
            // instance.
            base.OnStartup(e);

            // Check for admin privileges (some performance counters may need
            // it). WinMeters' manifest declares requireAdministrator (see
            // app.manifest); this warning fires when someone bypasses UAC by
            // launching via `dotnet run` or a non-elevated shell.
            if (!IsRunningAsAdmin())
            {
                WinMeters.Log.D("App: Not running as administrator. Some performance counters may be limited.");
            }

            // Load settings (with backup/restore support)
            var settings = AppSettings.Load();
            WinMeters.Log.D($"App starting. Settings loaded (Accent={settings.Colors.Accent}).");
        }

        /// <summary>
        /// Releases the single-instance mutex WITHOUT exiting the process.
        /// Used by the Restart menu command (cmd 1010 in
        /// <c>MainWindow.WmRButtonUp</c>) to let the freshly-launched
        /// WinMeters instance take ownership of the mutex immediately,
        /// avoiding the "already running" race where the new process
        /// tries to acquire the mutex before <see cref="OnExit"/> has
        /// released it.
        ///
        /// After this call, the static field is nulled out so the
        /// subsequent <see cref="OnExit"/>'s null check skips the
        /// release path — no double-release of the kernel handle, and
        /// the OS will auto-release any remaining handle on process
        /// exit if the field were somehow not null.
        /// </summary>
        public static void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex is { } mutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    WinMeters.Log.D($"App.ReleaseSingleInstanceMutex: ReleaseMutex failed: {ex.Message}");
                }

                try { mutex.Dispose(); }
                catch (Exception ex) { WinMeters.Log.D($"App.ReleaseSingleInstanceMutex: dispose failed: {ex.Message}"); }

                _singleInstanceMutex = null;
            }
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
