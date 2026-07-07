# Changelog

## Unreleased - 2026-05-15

- Refactor: Centralized PerformanceCounter creation and warm-up (`Monitors/MonitorManager.cs`).
- Refactor: Replaced PowerShell GPU query with WMI (`GetGpuDedicatedCapacityFromWmi`).
- Refactor: Added `CreateAndWarmCounter` and `DisposeQuietly` helpers.
- Cleanup: Consolidated debug logging into `Utils/Log.cs` and replaced scattered `Debug.WriteLine` calls.
- Refactor: Extracted sensor reset helper (`Monitors/HardwareMonitorService.cs`).
- Maintenance: Ran `dotnet format` and verified build.
