using LibreHardwareMonitor.Hardware;

namespace WinMeters.Monitors
{
    public class HardwareMonitorService : IDisposable
    {
        private readonly Computer? _computer;
        private bool _disposed;
        private bool _isInitialized;

        // Per-IHardware "is this a GPU?" flag computed once on first sight of the hardware
        // object. LibreHardwareMonitor returns stable IHardware instances across Update() calls,
        // so caching avoids re-running the `hardware.Name.Contains("...")` OrdinalIgnoreCase
        // scan (a hidden allocation each tick) on its 1s-tick update loop.
        //
        // ReferenceEqualityComparerIdentity gives us object-identity-based hashing for the
        // IHardware interface (no Equals/GetHashCode contract on it), and we deliberately do
        // not key on Name because Zotac vs GeForce "GPU" vs another OEM differ only in Name.
        private readonly Dictionary<IHardware, bool> _isGpuByHardware =
            new(ReferenceEqualityComparer.Instance);

        public float? CpuTemperature { get; private set; }
        public float? CpuLoad { get; private set; }
        public float? GpuTemperature { get; private set; }
        public float? GpuLoad { get; private set; }
        public string? GpuName { get; private set; }
        public float? GpuDedicatedMemoryUsage { get; private set; }
        public float? GpuSharedMemoryUsage { get; private set; }
        public float? GpuDedicatedMemoryUsed { get; private set; }
        public float? GpuDedicatedMemoryTotal { get; private set; }
        public float? GpuSharedMemoryUsed { get; private set; }
        public float? GpuSharedMemoryTotal { get; private set; }

        public bool IsAvailable => _isInitialized && _computer != null && HardwareCount > 0;
        public int HardwareCount => _computer?.Hardware?.Count ?? 0;

        public HardwareMonitorService(bool enableCpu = true, bool enableGpu = true, bool enableMotherboard = true)
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = enableCpu,
                    IsGpuEnabled = enableGpu,
                    IsMotherboardEnabled = enableMotherboard
                };
                _computer.Open();
                _isInitialized = true;

                foreach (var hardware in _computer.Hardware)
                {
                    if ((hardware.HardwareType == HardwareType.GpuNvidia ||
                         hardware.HardwareType == HardwareType.GpuAmd ||
                         hardware.HardwareType == HardwareType.GpuIntel) &&
                        string.IsNullOrEmpty(GpuName))
                    {
                        GpuName = hardware.Name;
                    }
                }

                WinMeters.Log.D($"HardwareMonitorService initialized. Hardware count: {_computer.Hardware.Count}, GPU: {GpuName}");
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HardwareMonitorService init failed: {ex.Message}");
                _isInitialized = false;
            }
        }

        public void Update()
        {
            if (!_isInitialized || _computer == null) return;

            try
            {
                CpuTemperature = null;
                CpuLoad = null;
                GpuTemperature = null;
                GpuLoad = null;
                GpuDedicatedMemoryUsage = null;
                GpuSharedMemoryUsage = null;
                GpuDedicatedMemoryUsed = null;
                GpuDedicatedMemoryTotal = null;
                GpuSharedMemoryUsed = null;
                GpuSharedMemoryTotal = null;

                _computer.Accept(new UpdateVisitor());

                foreach (var hardware in _computer.Hardware)
                {
                    bool isGpu = ClassifyAsGpu(hardware);

                    if (hardware.HardwareType == HardwareType.Cpu)
                        ProcessCpuSensors(hardware);
                    else if (isGpu)
                        ProcessGpuSensors(hardware);
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HardwareMonitorService update failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines whether an IHardware is a GPU. Prefers <see cref="HardwareType"/> (always
        /// trustworthy for Nvidia/AMD/Intel) and only falls back to name probing on
        /// <see cref="HardwareType.Unknown"/> to avoid naming arbitrary hardware (e.g., a NIC
        /// manufacturer whose name happens to contain "GPU") as a video card. Cached per-
        /// instance so the name scan doesn't run every Update() tick.
        /// </summary>
        private bool ClassifyAsGpu(IHardware hardware)
        {
            if (_isGpuByHardware.TryGetValue(hardware, out bool cached))
                return cached;

            bool isGpu = hardware.HardwareType switch
            {
                HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => true,
                _ => hardware.Name.AsSpan().Contains("GPU", StringComparison.OrdinalIgnoreCase)
                     || (hardware.Name.AsSpan().Contains("Graphics", StringComparison.OrdinalIgnoreCase)
                         && !hardware.Name.AsSpan().Contains("CPU", StringComparison.OrdinalIgnoreCase)),
            };
            _isGpuByHardware[hardware] = isGpu;
            return isGpu;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try
                {
                    _isGpuByHardware.Clear();
                }
                catch (Exception ex)
                {
                    // Never throw out of Dispose.
                    WinMeters.Log.D($"HardwareMonitorService cache clear: {ex.Message}");
                }
            }
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HardwareMonitorService dispose: {ex.Message}");
            }
            _isInitialized = false;
        }

        private void ProcessCpuSensors(IHardware hardware)
        {
            float? packageTemp = null;
            float? fallbackTemp = null;
            bool foundPackageTemp = false;

            foreach (var sensor in hardware.Sensors)
            {
                switch (sensor.SensorType)
                {
                    case SensorType.Temperature when sensor.Value.HasValue:
                        if (!foundPackageTemp && sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                        {
                            packageTemp = sensor.Value;
                            foundPackageTemp = true;
                        }
                        else if (!foundPackageTemp &&
                                 (sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                  sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)))
                        {
                            packageTemp ??= sensor.Value;
                        }
                        else if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            fallbackTemp ??= sensor.Value;
                        }
                        break;

                    case SensorType.Load when sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                        CpuLoad = sensor.Value;
                        break;
                }
            }

            CpuTemperature = packageTemp ?? fallbackTemp;
        }

        private void ProcessGpuSensors(IHardware hardware)
        {
            foreach (var sensor in hardware.Sensors)
            {
                switch (sensor.SensorType)
                {
                    case SensorType.Temperature when sensor.Value.HasValue && !GpuTemperature.HasValue &&
                        (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                         sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)):
                        GpuTemperature = sensor.Value;
                        break;

                    case SensorType.Load:
                        if (!GpuLoad.HasValue && IsGpuCoreLoadSensor(sensor))
                            GpuLoad = sensor.Value;
                        ProcessGpuMemorySensor(sensor);
                        break;

                    case SensorType.SmallData:
                        ProcessGpuMemorySensor(sensor);
                        break;
                }
            }

            if (!GpuDedicatedMemoryUsage.HasValue && GpuDedicatedMemoryUsed.HasValue && GpuDedicatedMemoryTotal.HasValue && GpuDedicatedMemoryTotal > 0)
                GpuDedicatedMemoryUsage = (GpuDedicatedMemoryUsed / GpuDedicatedMemoryTotal) * 100.0f;
            if (!GpuSharedMemoryUsage.HasValue && GpuSharedMemoryUsed.HasValue && GpuSharedMemoryTotal.HasValue && GpuSharedMemoryTotal > 0)
                GpuSharedMemoryUsage = (GpuSharedMemoryUsed / GpuSharedMemoryTotal.Value) * 100.0f;
        }

        private static bool IsGpuCoreLoadSensor(ISensor sensor)
        {
            ReadOnlySpan<char> n = sensor.Name.AsSpan();
            bool looksLikeCore = n.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                 n.Contains("Video", StringComparison.OrdinalIgnoreCase) ||
                                 n.Contains("Engine", StringComparison.OrdinalIgnoreCase) ||
                                 n.Contains("3D", StringComparison.OrdinalIgnoreCase);
            bool looksLikeMemory = n.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                                   n.Contains("VRAM", StringComparison.OrdinalIgnoreCase);
            return looksLikeCore && !looksLikeMemory;
        }

        private void ProcessGpuMemorySensor(ISensor sensor)
        {
            if (!sensor.Value.HasValue) return;
            ReadOnlySpan<char> n = sensor.Name.AsSpan();

            bool isMemory = n.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("Video", StringComparison.OrdinalIgnoreCase);
            if (!isMemory) return;

            bool isDedicated = n.Contains("Dedicated", StringComparison.OrdinalIgnoreCase) ||
                               n.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                               n.Contains("GPU Memory", StringComparison.OrdinalIgnoreCase) ||
                               (n.Contains("D3D", StringComparison.OrdinalIgnoreCase) && !n.Contains("Shared", StringComparison.OrdinalIgnoreCase));
            bool isShared = n.Contains("Shared", StringComparison.OrdinalIgnoreCase) ||
                            (n.Contains("System", StringComparison.OrdinalIgnoreCase) && n.Contains("GPU", StringComparison.OrdinalIgnoreCase)) ||
                            n.Equals("Memory Usage", StringComparison.OrdinalIgnoreCase);
            bool isTotal = n.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("Available", StringComparison.OrdinalIgnoreCase);

            if (sensor.SensorType == SensorType.Load)
            {
                if (isDedicated && !isTotal) GpuDedicatedMemoryUsage ??= sensor.Value;
                else if (isShared && !isTotal) GpuSharedMemoryUsage ??= sensor.Value;
            }
            else
            {
                if (isTotal)
                {
                    if (isDedicated) GpuDedicatedMemoryTotal ??= sensor.Value;
                    else if (isShared) GpuSharedMemoryTotal ??= sensor.Value;
                }
                else
                {
                    if (isDedicated) GpuDedicatedMemoryUsed ??= sensor.Value;
                    else if (isShared) GpuSharedMemoryUsed ??= sensor.Value;
                }
            }
        }

        // Note: no finalizer is added because Computer.Close() must be called from a managed
        // thread; finalizers can't safely free LibreHardwareMonitor internals on shutdown of
        // the runtime. SuppressFinalize still keeps the contract correct if a derived type
        // later does add a finalizer.
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    internal class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
