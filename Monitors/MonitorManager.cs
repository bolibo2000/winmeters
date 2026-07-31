using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Management;

namespace WinMeters.Monitors
{
    public class MonitorManager : IDisposable
    {
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _cpuUserCounter;
        private readonly PerformanceCounter _memCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;
        private readonly List<PerformanceCounter> _gpuDedicatedCounters = new();
        private readonly List<PerformanceCounter> _gpuSharedCounters = new();
        private readonly List<PerformanceCounter> _coreCounters = new();
        private readonly List<PerformanceCounter> _coreUserCounters = new();
        private readonly List<PerformanceCounter> _netRecvCounters = new();
        private readonly List<PerformanceCounter> _netSentCounters = new();

        private NetworkInterface[]? _cachedNetworkInterfaces;
        private long _lastInterfaceCacheTicks;
        private PerformanceCounterCategory? _cachedPhysicalDiskCategory;
        private long _lastDiskCacheTicks;
        private readonly float _cachedTotalRamMb;

        private string? _interfaceNameFilter;
        public string? InterfaceNameFilter
        {
            get => _interfaceNameFilter;
            set
            {
                if (_interfaceNameFilter != value)
                {
                    _interfaceNameFilter = value;
                    InitializeNetworkCounters();
                }
            }
        }

        public double CpuUsage { get; private set; }
        public double CpuUser { get; private set; }
        public double RamUsage { get; private set; }
        public double DiskReadUsage { get; private set; }
        public double DiskWriteUsage { get; private set; }
        public double NetDownload { get; private set; }
        public double NetUpload { get; private set; }
        public double GpuDedicatedUsage { get; private set; }
        public double GpuSharedUsage { get; private set; }
        public double GpuDedicatedTotal { get; private set; }
        public double GpuSharedTotal { get; private set; }
        public int LogicalCoreCount => _coreCounters.Count;

        public MonitorManager()
        {
            _cpuCounter = CreateAndWarmCounter("Processor", "% Processor Time", "_Total");
            _cpuUserCounter = CreateAndWarmCounter("Processor", "% User Time", "_Total");
            _memCounter = CreateAndWarmCounter("Memory", "Available MBytes", "");
            _diskReadCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Read Time", "_Total");
            _diskWriteCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Write Time", "_Total");
            _cachedTotalRamMb = GetTotalMemoryInMb();

            int environmentCores = Environment.ProcessorCount;
            int procCount = Math.Min(environmentCores, Constants.Hardware.MaxLogicalCoreCounters);
            if (environmentCores > Constants.Hardware.MaxLogicalCoreCounters)
            {
                WinMeters.Log.D($"MonitorManager: env ProcessorCount={environmentCores} exceeds MaxLogicalCoreCounters={Constants.Hardware.MaxLogicalCoreCounters}; per-core view truncated, _Total counter unaffected.");
            }
            for (int i = 0; i < procCount; i++)
            {
                try
                {
                    _coreCounters.Add(CreateAndWarmCounter("Processor", "% Processor Time", i.ToString()));
                    _coreUserCounters.Add(CreateAndWarmCounter("Processor", "% User Time", i.ToString()));
                }
                catch (Exception ex) { WinMeters.Log.D($"Failed to init core counters for index {i}: {ex}"); }
            }

            InitializeGpuCounters();
            InitializeNetworkCounters();
        }

        private void InitializeGpuCounters()
        {
            try
            {
                PerformanceCounterCategory? category;
                try { category = new PerformanceCounterCategory("GPU Adapter Memory"); }
                catch (Exception ex)
                {
                    WinMeters.Log.D($"GPU Adapter Memory category not found: {ex.Message}.");
                    // Defensive: clear any stale counter lists before WMI fallback so a half-built earlier
                    // state can't bleed through into UpdateGpu() reads after failure.
                    DisposeCounters(_gpuDedicatedCounters); _gpuDedicatedCounters.Clear();
                    DisposeCounters(_gpuSharedCounters); _gpuSharedCounters.Clear();
                    GpuDedicatedTotal = GetGpuDedicatedCapacityFromWmi();
                    GpuSharedTotal = GetGpuSharedCapacityFromWmi();
                    return;
                }

                _gpuDedicatedCounters.Clear();
                _gpuSharedCounters.Clear();

                int adapterCount = 0;
                foreach (var inst in category.GetInstanceNames())
                {
                    if (!inst.Contains("phys_0", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        _gpuDedicatedCounters.Add(CreateAndWarmCounter("GPU Adapter Memory", "Dedicated Usage", inst));
                        _gpuSharedCounters.Add(CreateAndWarmCounter("GPU Adapter Memory", "Shared Usage", inst));
                        if (++adapterCount >= Constants.Hardware.MaxGpuAdapters) break;
                    }
                    catch (Exception ex) { WinMeters.Log.D($"Failed to create GPU counter for {inst}: {ex.Message}"); }
                }

                GpuDedicatedTotal = GetGpuDedicatedCapacityFromWmi();
                GpuSharedTotal = GetGpuSharedCapacityFromWmi();
            }
            catch (Exception ex) { WinMeters.Log.D($"InitializeGpuCounters: {ex}"); }
        }

        private static double GetGpuDedicatedCapacityFromWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
                long maxRam = 0;
                foreach (ManagementObject mo in searcher.Get())
                {
                    if (mo["AdapterRAM"] != null && long.TryParse(mo["AdapterRAM"].ToString(), out long v) && v > maxRam)
                        maxRam = v;
                }
                if (maxRam > 0) return maxRam;
            }
            catch (Exception ex) { WinMeters.Log.D($"GetGpuDedicatedCapacityFromWmi: {ex}"); }
            return 4L * 1024 * 1024 * 1024;
        }

        private double GetGpuSharedCapacityFromWmi()
        {
            try
            {
                double totalRamBytes = GetTotalRamMb() * 1024.0 * 1024.0;
                return Math.Min(totalRamBytes * 0.5, 32L * 1024 * 1024 * 1024);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"GetGpuSharedCapacityFromWmi: {ex}");
                return 0;
            }
        }

        public (double Total, double User)[] GetCoreSplitUsages()
        {
            var results = new (double Total, double User)[_coreCounters.Count];
            for (int i = 0; i < _coreCounters.Count; i++)
            {
                try
                {
                    results[i] = (_coreCounters[i].NextValue(), _coreUserCounters[i].NextValue());
                }
                catch (Exception ex) { WinMeters.Log.D($"GetCoreSplitUsages index {i}: {ex}"); }
            }
            return results;
        }

        public float GetTotalRamMb() => _cachedTotalRamMb;

        private void InitializeNetworkCounters()
        {
            foreach (var c in _netRecvCounters) try { c.Dispose(); } catch (Exception ex) { WinMeters.Log.D($"Dispose netRecv counter: {ex.Message}"); }
            foreach (var c in _netSentCounters) try { c.Dispose(); } catch (Exception ex) { WinMeters.Log.D($"Dispose netSent counter: {ex.Message}"); }
            _netRecvCounters.Clear();
            _netSentCounters.Clear();

            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                foreach (var inst in category.GetInstanceNames())
                {
                    if (!string.IsNullOrWhiteSpace(InterfaceNameFilter) &&
                        !IsMatchingInstance(inst, InterfaceNameFilter)) continue;

                    try
                    {
                        _netRecvCounters.Add(CreateAndWarmCounter("Network Interface", "Bytes Received/sec", inst));
                        _netSentCounters.Add(CreateAndWarmCounter("Network Interface", "Bytes Sent/sec", inst));
                    }
                    catch (Exception ex) { WinMeters.Log.D($"Init network counter for '{inst}': {ex.Message}"); }
                }
            }
            catch (Exception ex) { WinMeters.Log.D($"InitializeNetworkCounters: {ex}"); }
        }

        private bool IsMatchingInstance(string perfInstance, string friendlyName)
        {
            try
            {
                long now = DateTime.UtcNow.Ticks;
                if (_cachedNetworkInterfaces == null || (now - _lastInterfaceCacheTicks) > Constants.Timing.CacheValidityTicks)
                {
                    _cachedNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                    _lastInterfaceCacheTicks = now;
                }

                var match = _cachedNetworkInterfaces.FirstOrDefault(nic =>
                    nic.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase));
                if (match is null) return false;

                string expected = match.Description.Replace('(', '[').Replace(')', ']');
                return perfInstance.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { WinMeters.Log.D($"IsMatchingInstance '{perfInstance}' vs '{friendlyName}': {ex.Message}"); }
            return false;
        }

        public void UpdateNet()
        {
            try
            {
                double totalRecv = 0, totalSent = 0;
                foreach (var c in _netRecvCounters) try { totalRecv += c.NextValue(); } catch (Exception ex) { WinMeters.Log.D($"UpdateNet recv: {ex.Message}"); }
                foreach (var c in _netSentCounters) try { totalSent += c.NextValue(); } catch (Exception ex) { WinMeters.Log.D($"UpdateNet sent: {ex.Message}"); }
                NetDownload = totalRecv;
                NetUpload = totalSent;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateNet: {ex}"); }
        }

        public void UpdateGpu()
        {
            try
            {
                double maxDedicatedBytes = 0;
                foreach (var counter in _gpuDedicatedCounters)
                    try { maxDedicatedBytes = Math.Max(maxDedicatedBytes, counter.NextValue()); } catch (Exception ex) { WinMeters.Log.D($"UpdateGpu dedicated: {ex.Message}"); }
                GpuDedicatedUsage = GpuDedicatedTotal > 0 ? Math.Clamp((maxDedicatedBytes / GpuDedicatedTotal) * 100.0, 0, 100) : 0;

                double maxSharedBytes = 0;
                foreach (var counter in _gpuSharedCounters)
                    try { maxSharedBytes = Math.Max(maxSharedBytes, counter.NextValue()); } catch (Exception ex) { WinMeters.Log.D($"UpdateGpu shared: {ex.Message}"); }
                GpuSharedUsage = GpuSharedTotal > 0 ? Math.Clamp((maxSharedBytes / GpuSharedTotal) * 100.0, 0, 100) : 0;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateGpu: {ex}"); }
        }

        public void UpdateCpu()
        {
            try
            {
                CpuUsage = ClampPercent(_cpuCounter.NextValue());
                CpuUser = ClampPercent(_cpuUserCounter.NextValue());
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateCpu: {ex}"); }
        }

        public void UpdateRam()
        {
            try
            {
                var availableMb = _memCounter.NextValue();
                RamUsage = _cachedTotalRamMb > 0 ? Math.Min(((_cachedTotalRamMb - availableMb) / _cachedTotalRamMb) * 100.0, 100) : 0;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateRam: {ex}"); }
        }

        public void UpdateDisk()
        {
            try
            {
                DiskReadUsage = ClampPercent(_diskReadCounter?.NextValue() ?? 0);
                DiskWriteUsage = ClampPercent(_diskWriteCounter?.NextValue() ?? 0);
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateDisk: {ex}"); }
        }

        /// <summary>
        /// Clamps a percentage to the canonical [0,100] range. Counter <c>NextValue()</c> calls have
        /// been observed to return negative values on the first sample and values slightly over 100
        /// mid-warmup; this avoids displaying "−1%" or "127%" on transient samples.
        /// </summary>
        private static double ClampPercent(double value) => Math.Clamp(value, 0.0, 100.0);

        public List<string> GetDiskInstances()
        {
            try
            {
                long now = DateTime.UtcNow.Ticks;
                if (_cachedPhysicalDiskCategory == null || (now - _lastDiskCacheTicks) > Constants.Timing.CacheValidityTicks)
                {
                    _cachedPhysicalDiskCategory = new PerformanceCounterCategory("PhysicalDisk");
                    _lastDiskCacheTicks = now;
                }

                var instances = _cachedPhysicalDiskCategory.GetInstanceNames().ToList();
                instances.Sort();
                if (instances.Contains("_Total"))
                {
                    instances.Remove("_Total");
                    instances.Insert(0, "_Total");
                }
                return instances;
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"GetDiskInstances: {ex}");
                return new List<string> { "_Total" };
            }
        }

        public void SetDiskInstance(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName)) instanceName = "_Total";
            try
            {
                if (_diskReadCounter != null && _diskReadCounter.InstanceName == instanceName) return;
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();
                _diskReadCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Read Time", instanceName);
                _diskWriteCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Write Time", instanceName);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"SetDiskInstance '{instanceName}': {ex}");
                try
                {
                    if (instanceName != "_Total")
                    {
                        _diskReadCounter = new PerformanceCounter("PhysicalDisk", "% Disk Read Time", "_Total", true);
                        _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "% Disk Write Time", "_Total", true);
                    }
                }
                catch (Exception fallbackEx) { WinMeters.Log.D($"SetDiskInstance '{instanceName}' _Total fallback: {fallbackEx.Message}"); }
            }
        }

        private float GetTotalMemoryInMb()
        {
            try
            {
                var memStatus = new NativeMethods.MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
                if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
                    return memStatus.ullTotalPhys / 1024f / 1024f;
            }
            catch (Exception ex) { WinMeters.Log.D($"GetTotalMemoryInMb: {ex}"); }
            return 0;
        }

        private static PerformanceCounter CreateAndWarmCounter(string category, string counter, string instance)
        {
            try
            {
                var c = string.IsNullOrEmpty(instance)
                    ? new PerformanceCounter(category, counter, true)
                    : new PerformanceCounter(category, counter, instance, true);
                // Warm-up NextValue() always throws on first call for many counter families
                // (InstanceNotExists etc.). Narrow the catch so a real ctor failure surfaces to
                // the caller below rather than being masked by warm-up noise.
                try { c.NextValue(); } catch (Exception warmupEx) { WinMeters.Log.D($"Warm-up {category}/{counter}/{instance}: {warmupEx.Message}"); }
                return c;
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"CreateAndWarmCounter failed for {category}/{counter}/{instance}: {ex}");
                throw;
            }
        }

        private static void DisposeCounters(IEnumerable<PerformanceCounter> list)
        {
            foreach (var c in list) try { c.Dispose(); } catch (Exception ex) { WinMeters.Log.D($"DisposeCounters: {ex.Message}"); }
        }

        public void Dispose()
        {
            try
            {
                _cpuCounter.Dispose();
                _cpuUserCounter.Dispose();
                _memCounter.Dispose();
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();

                DisposeCounters(_gpuDedicatedCounters); _gpuDedicatedCounters.Clear();
                DisposeCounters(_gpuSharedCounters); _gpuSharedCounters.Clear();
                DisposeCounters(_netRecvCounters); _netRecvCounters.Clear();
                DisposeCounters(_netSentCounters); _netSentCounters.Clear();
                DisposeCounters(_coreCounters); _coreCounters.Clear();
                DisposeCounters(_coreUserCounters); _coreUserCounters.Clear();

                _cachedPhysicalDiskCategory = null;
            }
            catch (Exception ex) { WinMeters.Log.D($"Dispose: {ex}"); }
        }
    }
}
