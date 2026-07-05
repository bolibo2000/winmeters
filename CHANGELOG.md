# Changelog

## Unreleased - 2026-05-15

- Refactor: Centralized PerformanceCounter creation and warm-up (`Monitors/MonitorManager.cs`).
- Refactor: Replaced PowerShell GPU query with WMI (`GetGpuDedicatedCapacityFromWmi`).
- Refactor: Added `CreateAndWarmCounter` and `DisposeQuietly` helpers.
- Cleanup: Consolidated debug logging into `Utils/Log.cs` and replaced scattered `Debug.WriteLine` calls.
- Refactor: Extracted sensor reset helper (`Monitors/HardwareMonitorService.cs`).
- Bugfix: Fixed VRAM/SRAM meter values by treating GPU Adapter Memory counters as bytes and converting to percentages (`Monitors/MonitorManager.cs`).
- Bugfix: Improved GPU memory sensor detection for LibreHardwareMonitor (handles "GPU Memory Used"/"GPU Memory Total") and removed the faulty Load>100==MB heuristic (`Monitors/HardwareMonitorService.cs`).
- Bugfix: VRAM pie now prefers an MB-derived ratio from LibreHardwareMonitor, avoiding the 4 GB overflow cap of `Win32_VideoController.AdapterRAM` (`MainWindow.xaml.cs`).
- Maintenance: Ran `dotnet format` and verified build.
