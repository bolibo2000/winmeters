using LibreHardwareMonitor.Hardware;

namespace WinMeters.Monitors
{
    /// <summary>
    /// Provides hardware sensor data using LibreHardwareMonitor.
    /// Requires administrator privileges for full functionality.
    /// </summary>
    public class HardwareMonitorService : IDisposable
    {
        private readonly Computer? _computer;
        private bool _disposed;
        private bool _isInitialized;

        /// <summary>CPU Package/Core temperature in °C.</summary>
        public float? CpuTemperature { get; private set; }

        /// <summary>CPU Total Load percentage.</summary>
        public float? CpuLoad { get; private set; }

        /// <summary>GPU temperature in °C.</summary>
        public float? GpuTemperature { get; private set; }

        /// <summary>CPU fan speed in RPM.</summary>
        public float? CpuFanSpeed { get; private set; }

        /// <summary>GPU fan speed in RPM.</summary>
        public float? GpuFanSpeed { get; private set; }

        /// <summary>CPU package power consumption in Watts.</summary>
        public float? CpuPackagePower { get; private set; }

        /// <summary>GPU power consumption in Watts.</summary>
        public float? GpuPower { get; private set; }

        /// <summary>GPU load percentage.</summary>
        public float? GpuLoad { get; private set; }

        /// <summary>CPU name/model.</summary>
        public string? CpuName { get; private set; }

        /// <summary>GPU name/model.</summary>
        public string? GpuName { get; private set; }

        /// <summary>GPU dedicated memory usage percentage.</summary>
        public float? GpuDedicatedMemoryUsage { get; private set; }
        /// <summary>GPU shared memory usage percentage.</summary>
        public float? GpuSharedMemoryUsage { get; private set; }
        /// <summary>GPU dedicated memory used in MB.</summary>
        public float? GpuDedicatedMemoryUsed { get; private set; }
        /// <summary>GPU dedicated memory total in MB.</summary>
        public float? GpuDedicatedMemoryTotal { get; private set; }
        /// <summary>GPU shared memory used in MB.</summary>
        public float? GpuSharedMemoryUsed { get; private set; }
        /// <summary>GPU shared memory total in MB.</summary>
        public float? GpuSharedMemoryTotal { get; private set; }

        /// <summary>Whether the service is properly initialized with detected hardware.</summary>
        public bool IsAvailable => _isInitialized && _computer != null && HardwareCount > 0;

        /// <summary>Number of hardware items detected.</summary>
        public int HardwareCount => _computer?.Hardware?.Count ?? 0;

        /// <summary>
        /// Initializes the hardware monitor.
        /// </summary>
        /// <param name="enableCpu">Enable CPU monitoring.</param>
        /// <param name="enableGpu">Enable GPU monitoring.</param>
        /// <param name="enableMotherboard">Enable motherboard monitoring (for fan sensors).</param>
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

                // Get initial hardware names
                foreach (var hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.Cpu && string.IsNullOrEmpty(CpuName))
                    {
                        CpuName = hardware.Name;
                    }
                    else if ((hardware.HardwareType == HardwareType.GpuNvidia ||
                              hardware.HardwareType == HardwareType.GpuAmd ||
                              hardware.HardwareType == HardwareType.GpuIntel) &&
                             string.IsNullOrEmpty(GpuName))
                    {
                        GpuName = hardware.Name;
                    }
                }

                WinMeters.Log.D($"HardwareMonitorService initialized. Hardware count: {_computer.Hardware.Count}, CPU: {CpuName}, GPU: {GpuName}");
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HardwareMonitorService init failed: {ex.Message}");
                _isInitialized = false;
            }
        }

        /// <summary>
        /// Updates all sensor values using the visitor pattern.
        /// </summary>
        public void Update()
        {
            if (!_isInitialized || _computer == null) return;

            try
            {
                // Reset GPU memory values before each update to prevent stale data
                GpuDedicatedMemoryUsage = null;
                GpuSharedMemoryUsage = null;
                GpuDedicatedMemoryUsed = null;
                GpuDedicatedMemoryTotal = null;
                GpuSharedMemoryUsed = null;
                GpuSharedMemoryTotal = null;

                // Use the visitor pattern to recursively update all hardware (key technique from LibreHWMonitor).
                // The visitor already runs hardware.Update() for every node, so we don't need to re-update here —
                // we just need to read sensors from any node that contains them.
                _computer.Accept(new UpdateVisitor());

                foreach (var hardware in _computer.Hardware)
                {
                    ProcessHardware(hardware);

                    // Sub-hardware (e.g. SuperIO children of Motherboard) carries CPU and chassis fan
                    // sensors that the top-level hardware doesn't expose. They were updated by the visitor
                    // above; this pass only reads them.
                    foreach (var subHardware in hardware.SubHardware)
                    {
                        ProcessSubHardware(subHardware, hardware.HardwareType);
                    }
                }
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HardwareMonitorService update failed: {ex.Message}");
            }
        }

        private void ProcessHardware(IHardware hardware)
        {
            // Check if this is a GPU by HardwareType OR by name (Intel iGPU may be reported as Unknown)
            bool isGpu = hardware.HardwareType == HardwareType.GpuNvidia ||
                         hardware.HardwareType == HardwareType.GpuAmd ||
                         hardware.HardwareType == HardwareType.GpuIntel ||
                         hardware.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                         (hardware.Name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) && !hardware.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));

            if (hardware.HardwareType == HardwareType.Cpu || isGpu)
            {
                if (isGpu)
                {
                    ProcessGpuSensors(hardware);
                }
                else
                {
                    ProcessCpuSensors(hardware);
                }
            }
        }

        private void ProcessSubHardware(IHardware subHardware, HardwareType parentType)
        {
            // SuperIO chips often have fan sensors
            if (subHardware.HardwareType == HardwareType.SuperIO)
            {
                foreach (var sensor in subHardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Fan && sensor.Value.HasValue)
                    {
                        // Try to identify CPU fan by name
                        if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) && !CpuFanSpeed.HasValue)
                        {
                            CpuFanSpeed = sensor.Value;
                        }
                    }
                }
            }
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
                    case SensorType.Temperature:
                        if (!sensor.Value.HasValue) break;

                        if (!foundPackageTemp && sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                        {
                            packageTemp = sensor.Value;
                            foundPackageTemp = true;
                        }
                        else if (!foundPackageTemp &&
                                 (sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                  sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!packageTemp.HasValue) packageTemp = sensor.Value;
                        }
                        else if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!fallbackTemp.HasValue) fallbackTemp = sensor.Value;
                        }
                        break;

                    case SensorType.Power:
                        if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            CpuPackagePower = sensor.Value;
                        break;

                    case SensorType.Fan:
                        if (!CpuFanSpeed.HasValue && sensor.Value.HasValue)
                            CpuFanSpeed = sensor.Value;
                        break;

                    case SensorType.Load:
                        if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
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
                    case SensorType.Temperature:
                        if (!sensor.Value.HasValue) break;

                        // Priority: "GPU Core" > "GPU Temperature" > first available GPU temp
                        if (!GpuTemperature.HasValue)
                        {
                            if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                            {
                                GpuTemperature = sensor.Value;
                            }
                            else if (sensor.Name.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase))
                            {
                                GpuTemperature = sensor.Value;
                            }
                            else if (sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                            {
                                GpuTemperature = sensor.Value;
                            }
                        }
                        break;

                    case SensorType.Power:
                        // Prefer "Total" sensor; fall back to first available if no "Total" exists yet
                        if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) && sensor.Value.HasValue)
                        {
                            GpuPower = sensor.Value;
                        }
                        else if (!GpuPower.HasValue && sensor.Value.HasValue)
                        {
                            GpuPower = sensor.Value;
                        }
                        break;

                    case SensorType.Fan:
                        if (!GpuFanSpeed.HasValue)
                        {
                            GpuFanSpeed = sensor.Value;
                        }
                        break;

                    case SensorType.SmallData:
                    case SensorType.Load:
                        // Memory/VRAM/Video sensors report usage as either:
                        // - Load: percentage (0-100)
                        // - SmallData: amount in MB
                        bool isMemoryRelated = sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                                               sensor.Name.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                                               sensor.Name.Contains("Video", StringComparison.OrdinalIgnoreCase);

                        if (isMemoryRelated && sensor.Value.HasValue)
                        {
                            string n = sensor.Name;

                            // "GPU Memory" and "VRAM" are normally dedicated GPU memory. D3D shared
                            // sensors are explicitly tagged with "Shared" / "System".
                            bool isDedicatedName = n.Contains("Dedicated", StringComparison.OrdinalIgnoreCase) ||
                                                   n.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                                                   n.Contains("GPU Memory", StringComparison.OrdinalIgnoreCase) ||
                                                   (n.Contains("D3D", StringComparison.OrdinalIgnoreCase) && !n.Contains("Shared", StringComparison.OrdinalIgnoreCase));
                            bool isSharedName = n.Contains("Shared", StringComparison.OrdinalIgnoreCase) ||
                                                (n.Contains("System", StringComparison.OrdinalIgnoreCase) && n.Contains("GPU", StringComparison.OrdinalIgnoreCase)) ||
                                                n.Equals("Memory Usage", StringComparison.OrdinalIgnoreCase);
                            bool isTotalName = n.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                                               n.Contains("Available", StringComparison.OrdinalIgnoreCase);

                            if (sensor.SensorType == SensorType.Load)
                            {
                                // Load sensors are percentages by definition.
                                if (isDedicatedName && !isTotalName)
                                {
                                    GpuDedicatedMemoryUsage ??= sensor.Value;
                                }
                                else if (isSharedName && !isTotalName)
                                {
                                    GpuSharedMemoryUsage ??= sensor.Value;
                                }
                            }
                            else // SmallData
                            {
                                // SmallData sensors report amounts in MB.
                                if (isTotalName)
                                {
                                    if (isDedicatedName)
                                        GpuDedicatedMemoryTotal ??= sensor.Value;
                                    else if (isSharedName)
                                        GpuSharedMemoryTotal ??= sensor.Value;
                                }
                                else
                                {
                                    if (isDedicatedName)
                                        GpuDedicatedMemoryUsed ??= sensor.Value;
                                    else if (isSharedName)
                                        GpuSharedMemoryUsed ??= sensor.Value;
                                }
                            }
                        }
                        else if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            GpuLoad = sensor.Value;
                        }
                        break;
                }
            }

            // Fallback calculation for usage if load sensor is missing.
            // Only compute when we have a denominator that matches the memory pool we're measuring.
            if (!GpuDedicatedMemoryUsage.HasValue &&
                GpuDedicatedMemoryUsed.HasValue &&
                GpuDedicatedMemoryTotal.HasValue &&
                GpuDedicatedMemoryTotal > 0)
            {
                GpuDedicatedMemoryUsage = (GpuDedicatedMemoryUsed / GpuDedicatedMemoryTotal) * 100.0f;
            }
            if (!GpuSharedMemoryUsage.HasValue &&
                GpuSharedMemoryUsed.HasValue &&
                GpuSharedMemoryTotal.HasValue &&
                GpuSharedMemoryTotal > 0)
            {
                // Do NOT fall back to GpuDedicatedMemoryTotal here: dedicated and shared come from
                // distinct memory pools on most GPUs and conflating them produces nonsensical ratios.
                GpuSharedMemoryUsage = (GpuSharedMemoryUsed / GpuSharedMemoryTotal.Value) * 100.0f;
            }
        }

        /// <summary>
        /// Disposes the hardware monitor and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                WinMeters.Log.D($"HardwareMonitorService dispose error: {ex.Message}");
            }

            // Mark uninitialized so IsAvailable reads as false after Dispose.
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Visitor pattern implementation for updating hardware sensors.
    /// This ensures all hardware and sub-hardware are properly updated.
    /// </summary>
    internal class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
