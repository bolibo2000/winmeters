using System;
using System.Runtime.InteropServices;

namespace Kil0bitSystemMonitor.Services
{
    /// <summary>
    /// Reads AMD iGPU temperature via ADL (AMD Display Library).
    /// Uses atiadlxx.dll already installed with the AMD Radeon driver.
    /// No kernel driver, no WinRing0, no admin required.
    /// </summary>
    public sealed class AmdAdlService : IDisposable
    {
        // --- ADL constants ---
        private const int ADL_OK = 0;
        private const int ADL_MAX_PATH = 256;
        private const int ADL_PMLOG_MAX_SENSORS = 256;

        // Sensor indices from ADLSensorType enum (ext_ADL.h)
        private const int ADL_SENSOR_TEMPERATURE_EDGE    = 8;  // GPU core/edge temp
        private const int ADL_SENSOR_TEMPERATURE_HOTSPOT = 27; // GPU hotspot
        private const int ADL_SENSOR_TEMPERATURE_GFX     = 28; // GFX die temp
        private const int ADL_SENSOR_GFX_ACTIVITY = 19; // GPU engine usage %

        // --- ADL structs ---
        [StructLayout(LayoutKind.Sequential)]
        private struct ADLAdapterInfo
        {
            public int Size;
            public int AdapterIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
            public string UDID;
            public int BusNumber;
            public int DeviceNumber;
            public int FunctionNumber;
            public int VendorID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
            public string AdapterName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
            public string DisplayName;
            public int Present;
            public int Exist;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
            public string DriverPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
            public string DriverPathExt;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
            public string PNPString;
            public int OSDisplayIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ADLSingleSensorData
        {
            public int Supported; // 1 = supported
            public int Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ADLPMLogDataOutput
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = ADL_PMLOG_MAX_SENSORS)]
            public ADLSingleSensorData[] Sensors;
        }

        // --- ADL delegate types (loaded dynamically from atiadlxx.dll) ---
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ADL_Main_Memory_AllocDelegate(int size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ADL2_Main_Control_CreateDelegate(
            ADL_Main_Memory_AllocDelegate callback, int enumConnectedAdapters, out IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ADL2_Main_Control_DestroyDelegate(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ADL2_Adapter_NumberOfAdapters_GetDelegate(IntPtr context, out int numAdapters);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ADL2_Adapter_AdapterInfo_GetDelegate(
            IntPtr context, IntPtr info, int inputSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ADL2_New_QueryPMLogData_GetDelegate(
            IntPtr context, int adapterIndex, ref ADLPMLogDataOutput dataOutput);

        // --- Win32 for DLL loading ---
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        // --- State ---
        private IntPtr _hDll = IntPtr.Zero;
        private IntPtr _adlContext = IntPtr.Zero;
        private int _adapterIndex = -1;
        private bool _initialized = false;

        private ADL2_Main_Control_DestroyDelegate? _destroy;
        private ADL2_New_QueryPMLogData_GetDelegate? _queryPmLog;

        // Memory alloc callback — ADL calls this to allocate buffers
        private static IntPtr AllocBuffer(int size) => Marshal.AllocHGlobal(size);

        public bool IsAvailable => _initialized && _adapterIndex >= 0 && _queryPmLog != null;

        private readonly string _selectedName;

        public AmdAdlService(string selectedGpuName)
        {
            _selectedName = selectedGpuName;
            TryInitialize();
        }

        private void TryInitialize()
        {
            try
            {
                // atiadlxx.dll is the 64-bit AMD Display Library — ships with Radeon driver
                _hDll = LoadLibrary("atiadlxx.dll");
                if (_hDll == IntPtr.Zero)
                {
                    // 32-bit fallback
                    _hDll = LoadLibrary("atiadlxy.dll");
                    if (_hDll == IntPtr.Zero) return;
                }

                var create = GetDelegate<ADL2_Main_Control_CreateDelegate>("ADL2_Main_Control_Create");
                _destroy   = GetDelegate<ADL2_Main_Control_DestroyDelegate>("ADL2_Main_Control_Destroy");
                var getCount  = GetDelegate<ADL2_Adapter_NumberOfAdapters_GetDelegate>("ADL2_Adapter_NumberOfAdapters_Get");
                var getInfo   = GetDelegate<ADL2_Adapter_AdapterInfo_GetDelegate>("ADL2_Adapter_AdapterInfo_Get");
                _queryPmLog   = GetDelegate<ADL2_New_QueryPMLogData_GetDelegate>("ADL2_New_QueryPMLogData_Get");

                if (create == null || _destroy == null || getCount == null ||
                    getInfo == null || _queryPmLog == null) return;

                // 1 = enumerate connected adapters only
                if (create(AllocBuffer, 1, out _adlContext) != ADL_OK) return;

                if (getCount(_adlContext, out int numAdapters) != ADL_OK || numAdapters <= 0) return;

                // Read adapter info array
                int structSize = Marshal.SizeOf(typeof(ADLAdapterInfo));
                IntPtr pInfo = Marshal.AllocHGlobal(structSize * numAdapters);
                try
                {
                    int infoResult = getInfo(_adlContext, pInfo, structSize * numAdapters);
                    if (infoResult != ADL_OK) return;

                    for (int i = 0; i < numAdapters; i++)
                    {
                        var info = Marshal.PtrToStructure<ADLAdapterInfo>(pInfo + i * structSize);
                        if (info.Present != 1 || (info.VendorID != 0x1002 && info.VendorID != 1002)) continue;

                        // match by name if specified, else take first AMD adapter
                        bool nameMatch = string.IsNullOrEmpty(_selectedName) ||
                                        info.AdapterName.Contains(_selectedName, StringComparison.OrdinalIgnoreCase) ||
                                        _selectedName.Contains(info.AdapterName, StringComparison.OrdinalIgnoreCase);

                        if (nameMatch) { _adapterIndex = info.AdapterIndex; break; }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pInfo);
                }

                _initialized = _adapterIndex >= 0;
            }
            catch { }
        }

        /// <summary>
        /// Returns iGPU temperature in Celsius, or -1 if unavailable.
        /// Tries GFX die temp first (Cezanne/Rembrandt), falls back to edge temp.
        /// </summary>
        public float GetGpuTemperature()
        {
            if (!IsAvailable) return -1;
            try
            {
                var data = new ADLPMLogDataOutput
                {
                    Size = Marshal.SizeOf(typeof(ADLPMLogDataOutput)),
                    Sensors = new ADLSingleSensorData[ADL_PMLOG_MAX_SENSORS]
                };

                if (_queryPmLog!(_adlContext, _adapterIndex, ref data) != ADL_OK) return -1;

                // Try GFX die temp first (sensor 57) — most accurate for APU iGPU
                if (ADL_SENSOR_TEMPERATURE_GFX < data.Sensors.Length &&
                    data.Sensors[ADL_SENSOR_TEMPERATURE_GFX].Supported == 1)
                {
                    float gfx = data.Sensors[ADL_SENSOR_TEMPERATURE_GFX].Value;
                    if (gfx > 0 && gfx < 120) return gfx;
                }

                // Fallback: edge temp (sensor 8)
                if (ADL_SENSOR_TEMPERATURE_EDGE < data.Sensors.Length &&
                    data.Sensors[ADL_SENSOR_TEMPERATURE_EDGE].Supported == 1)
                {
                    float edge = data.Sensors[ADL_SENSOR_TEMPERATURE_EDGE].Value;
                    if (edge > 0 && edge < 120) return edge;
                }

                return -1;
            }
            catch { return -1; }
        }

        public float GetGpuUsage()
        {
            if (!IsAvailable) return -1;
            try
            {
                var data = new ADLPMLogDataOutput
                {
                    Size = Marshal.SizeOf(typeof(ADLPMLogDataOutput)),
                    Sensors = new ADLSingleSensorData[ADL_PMLOG_MAX_SENSORS]
                };

                if (_queryPmLog!(_adlContext, _adapterIndex, ref data) != ADL_OK) return -1;

                if (ADL_SENSOR_GFX_ACTIVITY < data.Sensors.Length &&
                    data.Sensors[ADL_SENSOR_GFX_ACTIVITY].Supported == 1)
                {
                    float usage = data.Sensors[ADL_SENSOR_GFX_ACTIVITY].Value;
                    if (usage >= 0 && usage <= 100) return usage;
                }

                return -1;
            }
            catch { return -1; }
        }

        private T? GetDelegate<T>(string name) where T : Delegate
        {
            if (_hDll == IntPtr.Zero) return null;
            IntPtr ptr = GetProcAddress(_hDll, name);
            if (ptr == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        public void Dispose()
        {
            try
            {
                if (_adlContext != IntPtr.Zero && _destroy != null)
                {
                    _destroy(_adlContext);
                    _adlContext = IntPtr.Zero;
                }
                if (_hDll != IntPtr.Zero)
                {
                    FreeLibrary(_hDll);
                    _hDll = IntPtr.Zero;
                }
            }
            catch { }
        }
    }
}
