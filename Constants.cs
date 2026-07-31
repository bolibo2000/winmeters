namespace WinMeters
{
    public static class Constants
    {
        public static class Display
        {
            public const double CpuBarHeight = 24.0;
            public const double CpuBarWidth = 10.0;
            public const double CpuBarMargin = 1.0;
        }

        /// <summary>
        /// Hardware-related tunables. The two hardest-coded magical values that previously
        /// lived inline in <c>MonitorManager</c> (a "128" core ceiling and a "4" GPU adapter
        /// ceiling) live here so callers can be reasoned about from a single place. Changing
        /// either of them affects <em>only</em> the max number of per-instance counters we
        /// spin up, never the aggregate values seen by the user.
        /// </summary>
        public static class Hardware
        {
            /// <summary>Hard cap on per-core performance counters we instantiate. Larger servers
            /// can report more than 128 logical cores; in that case the per-core bar view simply
            /// stops at this count while the aggregate <c>_Total</c> counters above are unaffected
            /// and continue to drive the top-line CPU value. Truncation is logged by the
            /// <c>MonitorManager</c> ctor.</summary>
            public const int MaxLogicalCoreCounters = 128;

            /// <summary>Maximum number of GPU adapters honored per <c>GPU Adapter Memory</c>
            /// performance-counter category. Multi-GPU systems are rare; this stops us from
            /// spawning extra counters for adapters that proved to be VR-dedicated virtual
            /// representations of a single physical GPU.</summary>
            public const int MaxGpuAdapters = 4;
        }

        public static class Timing
        {
            public const int MinTimerIntervalMs = 20;
            public const int MinValidationRateMs = 100;
            public const long CacheValidityTicks = 30_000_000L;
            public const int ClockRefreshMs = 1000;
        }

        public static class Files
        {
            public const string SettingsFileName = "settings.json";
            public const string SettingsBackupFileName = "settings.backup.json";
        }

        public static class Process
        {
            public const string SingleInstanceMutexName = "WinMeters_SingleInstance_Mutex";
            /// <summary>
            /// Once <see cref="Log.ErrorCount"/> exceeds this in a single session the dispatcher
            /// unhandled-exception sink gives up recovery and shuts the app down so a cascade
            /// of faults can't pop dialogs in an infinite loop.
            /// </summary>
            public const long MaxErrorsBeforeShutdown = 10;
        }

        public static class Hotkey
        {
            public const int HotkeyId = 9000;
        }
    }
}
