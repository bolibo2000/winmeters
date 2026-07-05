namespace WinMeters
{
    /// <summary>
    /// Application-wide constants organized by category to replace magic numbers.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Display-related constants for UI elements and layout.
        /// </summary>
        public static class Display
        {
            // CPU Bar dimensions
            public const double CpuBarHeight = 24.0;
            public const double CpuBarWidth = 10.0;
            public const double CpuBarMargin = 1.0;

            // RAM Meter dimensions
            public const double RamMeterRadius = 12.0;
        }

        /// <summary>
        /// Timer and refresh rate constants.
        /// </summary>
        public static class Timing
        {
            /// <summary>
            /// Minimum timer interval (lower bound for the global dispatcher timer).
            /// Use 50–100+ ms in practice; below 15 ms Windows timer resolution degrades.
            /// </summary>
            public const int MinTimerIntervalMs = 20;

            /// <summary>
            /// Minimum user-allowed refresh rate (ms). Prevents users from setting rates
            /// that would peg a CPU core or starve WPF rendering.
            /// </summary>
            public const int MinValidationRateMs = 100;

            /// <summary>Default refresh rate (1 second).</summary>
            public const int DefaultRefreshRateMs = 1000;

            /// <summary>Tooltip refresh interval (100 ms is plenty for hover-driven UI).</summary>
            public const int TooltipKeepaliveIntervalMs = 100;

            /// <summary>Cached enumerations of disk/network interfaces; 30 seconds.</summary>
            public const long CacheValidityTicks = 30_000_000L; // 30s in 100ns ticks

            /// <summary>Default refresh rate for the clock readout (1 second).</summary>
            public const int ClockRefreshMs = 1000;
        }

        /// <summary>
        /// Window positioning and sizing constraints.
        /// </summary>
        public static class Window
        {
            public const double MinWindowVisibleWidth = 50.0;
            public const double MinWindowVisibleHeight = 20.0;
            public const double DefaultWindowBottomOffset = 80.0;
        }

        /// <summary>
        /// Settings and configuration file names.
        /// </summary>
        public static class Files
        {
            public const string SettingsFileName = "settings.json";
            public const string SettingsBackupFileName = "settings.backup.json";
        }

        /// <summary>
        /// Single instance and process synchronization constants.
        /// </summary>
        public static class Process
        {
            public const string SingleInstanceMutexName = "WinMeters_SingleInstance_Mutex";
        }

        /// <summary>
        /// Global hotkey configuration constants.
        /// </summary>
        public static class Hotkey
        {
            public const int HotkeyId = 9000;
        }
    }
}
