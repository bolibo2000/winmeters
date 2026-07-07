using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Management;

namespace WinMeters.Monitors
{
    /// <summary>
    /// Manages system performance monitoring for CPU, RAM, Disk, and Network.
    /// </summary>
    public class MonitorManager : IDisposable
    {
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _cpuUserCounter;
        private readonly PerformanceCounter _cpuPrivCounter;
        private readonly PerformanceCounter _memCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;
        private readonly List<PerformanceCounter> _gpuDedicatedCounters = new();
        private readonly List<PerformanceCounter> _gpuSharedCounters = new();

        // Cache for network interfaces and disk instances
        private NetworkInterface[]? _cachedNetworkInterfaces;
        private long _lastInterfaceCacheTicks;
        private PerformanceCounterCategory? _cachedPhysicalDiskCategory;
        private long _lastDiskCacheTicks;
        private const long CACHE_VALIDITY_TICKS = 300000000; // 30 seconds

        // Network monitoring via PerformanceCounters (same source as Task Manager)
        private readonly List<PerformanceCounter> _netRecvCounters = new();
        private readonly List<PerformanceCounter> _netSentCounters = new();
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

        // Cached total RAM
        private float _cachedTotalRamMb = 0;

        /// <summary>Total CPU usage percentage.</summary>
        public double CpuUsage { get; private set; }
        /// <summary>User-mode CPU usage percentage.</summary>
        public double CpuUser { get; private set; }
        /// <summary>Privileged/System CPU usage percentage.</summary>
        public double CpuPriv { get; private set; }
        /// <summary>RAM usage percentage.</summary>
        public double RamUsage { get; private set; }

        /// <summary>Disk read activity percentage.</summary>
        public double DiskReadUsage { get; private set; }
        /// <summary>Disk write activity percentage.</summary>
        public double DiskWriteUsage { get; private set; }

        /// <summary>Network download speed in bytes/sec.</summary>
        public double NetDownload { get; private set; }
        /// <summary>Network upload speed in bytes/sec.</summary>
        public double NetUpload { get; private set; }

        /// <summary>GPU dedicated memory usage percentage (0-100).</summary>
        public double GpuDedicatedUsage { get; private set; }
        /// <summary>GPU shared memory usage percentage (0-100).</summary>
        public double GpuSharedUsage { get; private set; }
        /// <summary>Total GPU dedicated memory in bytes.</summary>
        public double GpuDedicatedTotal { get; private set; }
        /// <summary>Total GPU shared memory in bytes.</summary>
        public double GpuSharedTotal { get; private set; }



        // Core Counters
        private List<PerformanceCounter> _coreCounters = new List<PerformanceCounter>();
        private List<PerformanceCounter> _coreUserCounters = new List<PerformanceCounter>();

        /// <summary>Number of logical CPU cores being monitored.</summary>
        public int LogicalCoreCount => _coreCounters.Count;

        /// <summary>
        /// Initializes a new instance of the MonitorManager.
        /// </summary>
        public MonitorManager()
        {
            _cpuCounter = CreateAndWarmCounter("Processor", "% Processor Time", "_Total");
            _cpuUserCounter = CreateAndWarmCounter("Processor", "% User Time", "_Total");
            _cpuPrivCounter = CreateAndWarmCounter("Processor", "% Privileged Time", "_Total");
            _memCounter = CreateAndWarmCounter("Memory", "Available MBytes", "");
            _diskReadCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Read Time", "_Total");
            _diskWriteCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Write Time", "_Total");

            RefreshNetworkInterfaces();

            // Cache total RAM on startup
            _cachedTotalRamMb = GetTotalMemoryInMb();

            // Initialize per-core counters only up to a reasonable limit to save memory
            int procCount = Math.Min(Environment.ProcessorCount, 128);
            for (int i = 0; i < procCount; i++)
            {
                try
                {
                    _coreCounters.Add(CreateAndWarmCounter("Processor", "% Processor Time", i.ToString()));
                    _coreUserCounters.Add(CreateAndWarmCounter("Processor", "% User Time", i.ToString()));
                }
                catch (Exception ex)
                {
                    WinMeters.Log.D($"Failed to init core counters for index {i}: {ex}");
                }
            }

            InitializeGpuCounters();
        }

        private void InitializeGpuCounters()
        {
            try
            {
                PerformanceCounterCategory? category;
                try
                {
                    category = new PerformanceCounterCategory("GPU Adapter Memory");
                }
                catch (Exception ex)
                {
                    WinMeters.Log.D($"GPU Adapter Memory performance counter category not found: {ex.Message}. GPU memory counters will be unavailable.");
                    GpuDedicatedTotal = GetGpuDedicatedCapacityFromWmi();
                    GpuSharedTotal = GetGpuSharedCapacityFromWmi();
                    return;
                }

                var instances = category.GetInstanceNames();

                _gpuDedicatedCounters.Clear();
                _gpuSharedCounters.Clear();

                int adapterCount = 0;
                foreach (var inst in instances)
                {
                    if (inst.Contains("phys_0", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var ded = CreateAndWarmCounter("GPU Adapter Memory", "Dedicated Usage", inst);
                            var shr = CreateAndWarmCounter("GPU Adapter Memory", "Shared Usage", inst);

                            _gpuDedicatedCounters.Add(ded);
                            _gpuSharedCounters.Add(shr);

                            adapterCount++;
                            WinMeters.Log.D($"GPU adapter detected: {inst} (dedicated & shared usage counters initialized)");
                            if (adapterCount >= 4) break;
                        }
                        catch (Exception ex) { WinMeters.Log.D($"Failed to create GPU counter for {inst}: {ex.Message}"); }
                    }
                }

                if (adapterCount == 0)
                {
                    WinMeters.Log.D("No GPU adapters with Dedicated/Shared Usage counters detected.");
                }

                // Try to detect total dedicated memory from WMI (Sum of all adapters)
                GpuDedicatedTotal = GetGpuDedicatedCapacityFromWmi();
                GpuSharedTotal = GetGpuSharedCapacityFromWmi();
            }
            catch (Exception ex) { WinMeters.Log.D($"InitializeGpuCounters: {ex}"); }
        }

        private double GetGpuDedicatedCapacityFromWmi()
        {
            try
            {
                // Sum AdapterRAM for all video controllers
                try
                {
                    // Query WMI directly for AdapterRAM sum instead of spawning PowerShell
                    using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
                    long sum = 0;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo["AdapterRAM"] != null && long.TryParse(mo["AdapterRAM"].ToString(), out long v))
                        {
                            sum += v;
                        }
                    }
                    if (sum > 0) return sum;
                }
                catch { }
            }
            catch { }
            return 4L * 1024 * 1024 * 1024; // 4GB fallback
        }

        private double GetGpuSharedCapacityFromWmi()
        {
            try
            {
                // Query WMI for GPU shared memory limit. Win32_VideoController doesn't expose this
                // directly, so we read it from the registry on systems that expose it, or fall back
                // to a reasonable default based on system RAM.
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT ShareMode FROM Win32_VideoController");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo["ShareMode"] != null)
                        {
                            // ShareMode: 0 = None, 1 = ShareDMA, 2 = ShareInt
                            // This doesn't give us a size, but we can use it to determine iGPU presence
                        }
                    }
                }
                catch { }

                // For iGPU systems, shared memory is typically configurable in BIOS.
                // Without WMI/registry data, fall back to a system-dependent calculation:
                // Use 12.5% of total RAM for integrated GPUs (common default), capped at 4GB.
                double totalRamBytes = GetTotalRamMb() * 1024.0 * 1024.0;
                double sharedTotal = Math.Min(totalRamBytes * 0.125, 4L * 1024 * 1024 * 1024);

                WinMeters.Log.D($"GetGpuSharedCapacityFromWmi: using fallback {sharedTotal / (1024.0*1024.0*1024.0):F2} GB (12.5% of system RAM, capped at 4GB)");
                return sharedTotal;
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"GetGpuSharedCapacityFromWmi: {ex}");
                return 0; // fallback to 0 — caller should use system RAM as last resort
            }
        }

        /// <summary>
        /// Gets per-core CPU usage split into total and user percentages.
        /// </summary>
        public (double Total, double User)[] GetCoreSplitUsages()
        {
            var results = new (double Total, double User)[_coreCounters.Count];
            for (int i = 0; i < _coreCounters.Count; i++)
            {
                try
                {
                    double t = _coreCounters[i].NextValue();
                    double u = _coreUserCounters[i].NextValue();
                    results[i] = (t, u);
                }
                catch (Exception ex)
                {
                    WinMeters.Log.D($"GetCoreSplitUsages index {i}: {ex}");
                }
            }
            return results;
        }

        /// <summary>
        /// Gets total RAM in megabytes.
        /// </summary>
        public float GetTotalRamMb() => _cachedTotalRamMb;

        /// <summary>
        /// Initializes or reinitializes network PerformanceCounters.
        /// Uses the "Network Interface" category — the same data source as Task Manager.
        /// </summary>
        private void InitializeNetworkCounters()
        {
            // Dispose old counters
            foreach (var c in _netRecvCounters) try { c.Dispose(); } catch { }
            foreach (var c in _netSentCounters) try { c.Dispose(); } catch { }
            _netRecvCounters.Clear();
            _netSentCounters.Clear();

            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                var instances = category.GetInstanceNames();

                foreach (var inst in instances)
                {
                    // If user selected a specific interface, match by name
                    if (!string.IsNullOrWhiteSpace(InterfaceNameFilter))
                    {
                        // PerformanceCounter instance names use adapter description with special chars replaced
                        // Match against the friendly name from NetworkInterface
                        if (!IsMatchingInstance(inst, InterfaceNameFilter)) continue;
                    }

                    try
                    {
                        var recv = CreateAndWarmCounter("Network Interface", "Bytes Received/sec", inst);
                        var sent = CreateAndWarmCounter("Network Interface", "Bytes Sent/sec", inst);

                        _netRecvCounters.Add(recv);
                        _netSentCounters.Add(sent);
                    }
                    catch { /* Skip inaccessible counters */ }
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"InitializeNetworkCounters: {ex}");
            }
        }

        /// <summary>
        /// Checks if a PerformanceCounter instance name matches a friendly network interface name.
        /// PerfCounter names use the adapter description with brackets instead of parentheses.
        /// </summary>
        private bool IsMatchingInstance(string perfInstance, string friendlyName)
        {
            try
            {
                long now = DateTime.UtcNow.Ticks;
                if (_cachedNetworkInterfaces == null || (now - _lastInterfaceCacheTicks) > CACHE_VALIDITY_TICKS)
                {
                    _cachedNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                    _lastInterfaceCacheTicks = now;
                }

                foreach (var nic in _cachedNetworkInterfaces)
                {
                    if (nic.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        string expected = nic.Description.Replace('(', '[').Replace(')', ']');
                        return perfInstance.Equals(expected, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
            return false;
        }

        private void RefreshNetworkInterfaces()
        {
            InitializeNetworkCounters();
        }

        /// <summary>
        /// Resets network counters when interface changes.
        /// </summary>
        public void ResetNetworkCounters()
        {
            InitializeNetworkCounters();
        }

        /// <summary>
        /// Updates network download and upload speeds using PerformanceCounters.
        /// </summary>
        public void UpdateNet()
        {
            try
            {
                double totalRecv = 0;
                double totalSent = 0;

                foreach (var c in _netRecvCounters)
                {
                    try { totalRecv += c.NextValue(); } catch { }
                }
                foreach (var c in _netSentCounters)
                {
                    try { totalSent += c.NextValue(); } catch { }
                }

                NetDownload = totalRecv;
                NetUpload = totalSent;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateNet: {ex}"); }
        }

        public void Update()
        {
            UpdateCpu();
            UpdateRam();
            UpdateDisk();
            UpdateNet();
            UpdateGpu();
        }

        public void UpdateGpu()
        {
            try
            {
                // GPU Adapter Memory PerformanceCounter returns PERCENTAGE (0-100), not bytes.
                // Take the maximum usage across all detected GPUs for the primary reading.
                double maxDedicatedPct = 0;
                foreach (var counter in _gpuDedicatedCounters)
                {
                    try { maxDedicatedPct = Math.Max(maxDedicatedPct, counter.NextValue()); } catch { }
                }
                GpuDedicatedUsage = maxDedicatedPct;

                double maxSharedPct = 0;
                foreach (var counter in _gpuSharedCounters)
                {
                    try { maxSharedPct = Math.Max(maxSharedPct, counter.NextValue()); } catch { }
                }
                GpuSharedUsage = maxSharedPct;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateGpu: {ex}"); }
        }

        public void UpdateCpu()
        {
            try
            {
                CpuUsage = _cpuCounter.NextValue();
                CpuUser = _cpuUserCounter.NextValue();
                CpuPriv = _cpuPrivCounter.NextValue();

                if (CpuUsage > 100) CpuUsage = 100;
                if (CpuUser > 100) CpuUser = 100;
                if (CpuPriv > 100) CpuPriv = 100;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateCpu: {ex}"); }
        }

        public void UpdateRam()
        {
            try
            {
                var availableMb = _memCounter.NextValue();
                var totalMb = GetTotalMemoryInMb();
                if (totalMb > 0)
                {
                    RamUsage = ((totalMb - availableMb) / totalMb) * 100.0;
                }
                else
                {
                    RamUsage = 0;
                }
                if (RamUsage > 100) RamUsage = 100;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateRam: {ex}"); }
        }
        public void UpdateDisk()
        {
            try
            {
                DiskReadUsage = _diskReadCounter?.NextValue() ?? 0;
                DiskWriteUsage = _diskWriteCounter?.NextValue() ?? 0;

                if (DiskReadUsage > 100) DiskReadUsage = 100;
                if (DiskWriteUsage > 100) DiskWriteUsage = 100;
            }
            catch (Exception ex) { WinMeters.Log.D($"UpdateDisk: {ex}"); }
        }

        public List<string> GetDiskInstances()
        {
            try
            {
                long now = DateTime.UtcNow.Ticks;
                if (_cachedPhysicalDiskCategory == null || (now - _lastDiskCacheTicks) > CACHE_VALIDITY_TICKS)
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
                // Only recreate if different (or if it's the first time and we want to be sure)
                if (_diskReadCounter != null && _diskReadCounter.InstanceName == instanceName) return;

                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();

                _diskReadCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Read Time", instanceName);
                _diskWriteCounter = CreateAndWarmCounter("PhysicalDisk", "% Disk Write Time", instanceName);
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"SetDiskInstance '{instanceName}': {ex}");
                // Fallback
                try
                {
                    if (instanceName != "_Total")
                    {
                        _diskReadCounter = new PerformanceCounter("PhysicalDisk", "% Disk Read Time", "_Total", true);
                        _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "% Disk Write Time", "_Total", true);
                    }
                }
                catch { }
            }
        }



        private float GetTotalMemoryInMb()
        {
            try
            {
                var memStatus = new NativeMethods.MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));
                if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
                {
                    return memStatus.ullTotalPhys / 1024f / 1024f;
                }
            }
            catch (Exception ex) { WinMeters.Log.D($"GetTotalMemoryInMb: {ex}"); }
            return 0;
        }

        private static PerformanceCounter CreateAndWarmCounter(string category, string counter, string instance)
        {
            try
            {
                PerformanceCounter c;
                if (string.IsNullOrEmpty(instance))
                    c = new PerformanceCounter(category, counter, true);
                else
                    c = new PerformanceCounter(category, counter, instance, true);

                try { c.NextValue(); } catch { }
                return c;
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"CreateAndWarmCounter failed for {category}/{counter}/{instance}: {ex}");
                // Rethrow so caller can handle if necessary
                throw;
            }
        }

        private static void DisposeCounter(PerformanceCounter? c)
        {
            try { c?.Dispose(); } catch { }
        }

        private static void DisposeCounters(IEnumerable<PerformanceCounter> list)
        {
            if (list == null) return;
            foreach (var c in list) try { c.Dispose(); } catch { }
        }

        /// <summary>
        /// Dispose managed resources (PerformanceCounter objects)
        /// </summary>
        public void Dispose()
        {
            try
            {
                DisposeCounter(_cpuCounter);
                DisposeCounter(_cpuUserCounter);
                DisposeCounter(_cpuPrivCounter);
                DisposeCounter(_memCounter);
                DisposeCounter(_diskReadCounter);
                DisposeCounter(_diskWriteCounter);

                DisposeCounters(_gpuDedicatedCounters);
                _gpuDedicatedCounters.Clear();
                DisposeCounters(_gpuSharedCounters);
                _gpuSharedCounters.Clear();

                DisposeCounters(_netRecvCounters);
                _netRecvCounters.Clear();
                DisposeCounters(_netSentCounters);
                _netSentCounters.Clear();

                DisposeCounters(_coreCounters);
                _coreCounters.Clear();
                DisposeCounters(_coreUserCounters);
                _coreUserCounters.Clear();

                _cachedPhysicalDiskCategory = null;
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"Dispose: {ex}");
            }
        }
    }
}
