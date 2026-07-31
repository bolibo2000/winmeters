# Changelog

## 2.5.1 - 2026-07-20

- Refactor: Extracted native Win32 popup menu into `Services/BarPopupMenuService` with `IBarMenuDelegate` interface (`MainWindow.xaml.cs`).
- Refactor: Promoted per-window shims (SectionHeader, Slider, CheckBox, etc.) into theme-level `WinMeters*` styles (`Themes/WinMetersTheme.xaml`).
- Refactor: Centralized PerformanceCounter creation and warm-up (`Monitors/MonitorManager.cs`).
- Refactor: Replaced PowerShell GPU query with WMI (`GetGpuDedicatedCapacityFromWmi`).
- Refactor: Added `CreateAndWarmCounter` and `DisposeQuietly` helpers.
- Refactor: Extracted sensor reset helper (`Monitors/HardwareMonitorService.cs`).
- Bugfix: Fixed VRAM/SRAM meter values by treating GPU Adapter Memory counters as bytes and converting to percentages (`Monitors/MonitorManager.cs`).
- Bugfix: Improved GPU memory sensor detection for LibreHardwareMonitor (handles "GPU Memory Used"/"GPU Memory Total") and removed the faulty Load>100==MB heuristic (`Monitors/HardwareMonitorService.cs`).
- Bugfix: VRAM pie now prefers an MB-derived ratio from LibreHardwareMonitor, avoiding the 4 GB overflow cap of `Win32_VideoController.AdapterRAM` (`MainWindow.xaml.cs`).
- Bugfix: Restored `SetPreferredAppMode` in popup menu dark-mode handling so runtime theme switches take effect (`Services/BarPopupMenuService.cs`).
- Cleanup: Consolidated debug logging into `Utils/Log.cs` and replaced scattered `Debug.WriteLine` calls.
- Cleanup: Version read dynamically from assembly in About window instead of hardcoded string (`AboutWindow.xaml.cs`, `WinMeters.csproj`).
- Maintenance: Ran `dotnet format` and verified build.
