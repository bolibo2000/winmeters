using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinMeters
{
    /// <summary>
    /// Lightweight release-safe logger. Routes diagnostics to <c>Debug.WriteLine</c>
    /// (visible only in DEBUG) AND an optional rolling file at
    /// <c>%LOCALAPPDATA%\WinMeters\winmeters.log</c> when enabled. Errors (<see cref="E"/>)
    /// always write to the file so silent Release build failures (where Debug.WriteLine is
    /// compiled out) stay diagnosable.
    /// </summary>
    internal static class Log
    {
        public enum Level
        {
            Debug = 0,
            Error = 1
        }

        private static readonly object _fileLock = new();
        private static readonly Lazy<string> _logDir = new(ResolveLogDirectory, isThreadSafe: true);
        private static readonly Lazy<string> _logPath = new(() => Path.Combine(_logDir.Value, "winmeters.log"), isThreadSafe: true);
        private static long _errorCountThisSession;
        private const long MaxLogSizeBytes = 1L * 1024 * 1024; // 1 MB roll-over threshold
        private const int MaxArchivedFiles = 3;

        /// <summary>
        /// Enables or disables file logging at runtime. Defaults to enabled (Errors always
        /// go through). The first error after enabling flushes the configured directory.
        /// </summary>
        public static bool FileLoggingEnabled { get; set; } = true;

        public static void D(string message) => Write(Level.Debug, message);

        // Kept for back-compat with concurrent `Log.W`-style callers.
        public static void W(string message) => Write(Level.Debug, $"[Warn] {message}");

        public static void E(string message)
        {
            Interlocked.Increment(ref _errorCountThisSession);
            Write(Level.Error, message);
        }

        public static void E(Exception ex, string? context = null)
        {
            Interlocked.Increment(ref _errorCountThisSession);
            string payload = string.IsNullOrEmpty(context) ? ex.ToString() : $"{context}: {ex}";
            Write(Level.Error, payload);
        }

        /// <summary>Number of errors logged in the current process session.</summary>
        public static long ErrorCount => Interlocked.CompareExchange(ref _errorCountThisSession, 0, 0);

        private static void Write(Level level, string message)
        {
            string prefix = level == Level.Error ? "[ERR] " : "[DBG] ";
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {prefix}{message}";
            Debug.WriteLine(line);

            if (!FileLoggingEnabled || level == Level.Debug) return;

            try { AppendToFile(line); }
            catch { /* Last-ditch: never let logging itself throw. */ }
        }

        private static void AppendToFile(string line)
        {
            lock (_fileLock)
            {
                string path = _logPath.Value;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxLogSizeBytes)
                        RollArchive(path);
                }
                catch { /* roll failures shouldn't block the write */ }

                using var writer = new StreamWriter(path, append: true, Encoding.UTF8, bufferSize: 1024);
                writer.WriteLine(line);
				writer.Flush(); // Ensure immediate write for error logs
            }
        }

        private static void RollArchive(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? ".";
                // Drop oldest archive out and shift up.
                string oldest = $"{path}.{MaxArchivedFiles}";
                if (File.Exists(oldest)) File.Delete(oldest);

                for (int i = MaxArchivedFiles - 1; i >= 1; i--)
                {
                    string src = $"{path}.{i}";
                    string dst = $"{path}.{i + 1}";
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }
                string archived = $"{path}.1";
                if (File.Exists(archived)) File.Delete(archived);
                File.Move(path, archived);
            }
            catch
            {
                // Rolling failure is non-fatal; we'll just keep appending to the live file.
            }
        }

        private static string ResolveLogDirectory()
        {
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolderOption.DoNotVerify);
                return Path.Combine(baseDir, "WinMeters");
            }
            catch
            {
                // Absolutely no LocalAppData path (locked-down environment). Fallback to BaseDirectory so we don't throw.
                return AppContext.BaseDirectory;
            }
        }
    }
}
