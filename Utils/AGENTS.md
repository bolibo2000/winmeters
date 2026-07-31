# Utils Domain

## Purpose
Shared utilities with no UI or Win32 dependency: structured logging and pie chart bitmap rendering.

## Ownership
Owns `Utils/Log.cs`, `Utils/PieChartRenderer.cs`.

## Local Contracts

### Logging
- `Log.D` / `Log.W` / `Log.E` are the canonical logging API. No other file should write to `Debug.WriteLine` or directly to the log file.
- `Log.E` increments `Log.ErrorCount` (atomic); used by `App` to determine fatal shutdown threshold.
- File logging is enabled by default; set `Log.FileLoggingEnabled = false` to suppress file writes (e.g., in design-time mode).
- The log file rolls at `MaxLogSizeBytes` (1 MB) with up to `MaxArchivedFiles` (3) archive files kept.
- The log directory is resolved once via `Lazy<string>` and defaults to `%LOCALAPPDATA%\WinMeters`.

### Pie chart rendering
- `PieChartRenderer` produces `BitmapSource` for WPF `Image` controls via `WriteableBitmap` + GDI interop.
- DPI scaling uses a fixed `DpiBuckets` table — if a future Windows version reports a non-standard DPI, the code falls back to the nearest bucket but never renders at a wrong size.
- Cache key is `(percentage, dpiBucket)`; cache is invalidated when either changes by more than `CacheThresholdPercent` (0.1%).

## Work Guidance

### Adding a new utility class
- Place in `Utils/` if it has no dependencies outside the standard library.
- If it depends on WPF types, prefer `Utils/` over adding it to a window code-behind.
- Do not add `System.Windows` references here — keep `Utils` UI-framework-agnostic.

### Thread safety
- `Log._fileLock` is a static `object` — all file writes are serialized.
- `Log` lazy fields (`_logDir`, `_logPath`) use `Lazy<string>` with `isThreadSafe: true`.
- `PieChartRenderer` static methods are thread-safe — all state is on the call stack.

## Verification

`dotnet test Tests/WinMeters.Tests.csproj` — `ColorHelperTests`, `PieChartRendererTests`, `PieChartRendererDpiBucketTests`.

## Child DOX Index

<!-- No child AGENTS.md files in Utils/ -->