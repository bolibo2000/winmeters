# Monitors Domain

## Purpose
Hardware sensor polling (LibreHardwareMonitorLib) and OS performance counter management (Processor, Memory, PhysicalDisk, Network Interface, GPU Adapter Memory).

## Ownership
Owns `Monitors/*.cs`.

## Local Contracts

### Startup robustness
- `MonitorManager` constructor catches exceptions from `PerformanceCounter` creation and continues with defaults (logs the error). The UI remains functional with zeroed counters.
- `HardwareMonitorService` catches `Exception` during `Computer.Open()` and sets `IsAvailable=false`. All property reads remain null-safe.
- If the `GPU Adapter Memory` performance counter category is absent, GPU memory falls back to WMI `Win32_VideoController.AdapterRAM` (dedicated) and `GetTotalRamMb()*0.5` (shared, capped at 32 GB).

### Thread safety
- `PerformanceCounter.NextValue()` is documented as thread-safe by Microsoft but returns stale data under heavy concurrent sampling. Accept this — it is the expected behavior for per-tick UI updates.
- `HardwareMonitorService.Update()` is called from the UI thread only; no internal locking needed.

### Counter disposal
- All `PerformanceCounter` instances are held in `List<PerformanceCounter>` fields and disposed via `DisposeCounters(list)` in `MonitorManager.Dispose()`.
- `MonitorManager.Dispose()` is called from `MainWindow_Closed` and `MenuItem_Exit_Click`.

### Cache validity
- `NetworkInterface[]` cache and `PerformanceCounterCategory` cache use `Constants.Timing.CacheValidityTicks` (30 M ticks ≈ 3 seconds). Counters are refreshed when cache expires or the interface name filter changes.

## Work Guidance

### Adding a new performance counter family
1. Add fields to `MonitorManager` (counter, instance, etc.).
2. Add `Update*` method following the `UpdateCpu`/`UpdateRam` pattern (try/catch → log → assign field).
3. Add a `CreateAndWarmCounter` call in the constructor or a lazy init method.
4. If the category is optional (may not exist on some systems), wrap the creation in a `try/catch` and fall back gracefully.

### Adding a new hardware sensor
1. Add the nullable property to `HardwareMonitorService`.
2. Add a `Process*Sensor` method following `ProcessCpuSensors`/`ProcessGpuSensors`.
3. Classify hardware via `ClassifyAsGpu` (GPU) or by `HardwareType` switch.

## Verification

`dotnet test Tests/WinMeters.Tests.csproj` — no domain-specific tests yet. `MonitorManager` is exercised via integration tests.

## Child DOX Index

<!-- No child AGENTS.md files in Monitors/ -->