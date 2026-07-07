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
            public const double RamMeterSize = 24.0;
            public const double RamMeterRadius = 12.0;
        }

        /// <summary>
        /// Timer and refresh rate constants.
        /// </summary>
        public static class Timing
        {
            // Minimum timer interval (20ms = 50 FPS max)
            public const int MinTimerIntervalMs = 20;

            // Minimum validation rate for user input
            public const int MinValidationRateMs = 100;

            // Default refresh rate (1 second)
            public const int DefaultRefreshRateMs = 1000;

            // Network interface cache refresh interval (30 seconds)
            public const int NetworkInterfaceCacheRefreshMs = 30000;
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
