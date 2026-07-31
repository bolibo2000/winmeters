# Configuration Domain

## Purpose
Application settings (persistence, defaults, migration) and compile-time constants (display geometry, timing, hardware limits, file names, process tunables).

## Ownership
Owns `AppSettings.cs`, `Constants.cs`.

## Local Contracts

### Settings loading
- `AppSettings.Load()` tries primary path → backup path → creates defaults. Always saves defaults on first run.
- JSON serialization uses `JsonSerializerOptions { WriteIndented = true }` for human readability.
- Backup is written before every save; failure is non-fatal (logged and continue).
- Settings live next to the executable (`Path.GetDirectoryName(Environment.ProcessPath)`), not in AppData — portable by design.

### Settings migration
- `MigrateSettings` uses a two-gate approach: cheap `Has(rawJson, token)` substring probe for "is field absent?", then `JsonDocument` only when extraction is needed.
- `JsonReadBool` is tolerant: returns `true`/`false` for `"true"`/`"false"` strings, falls back to `defaultValue` for invalid input.
- Legacy key aliases (e.g., `"CPU Package"` → `"CpuTemp"`) are normalized in `MigrateSettings` before use.
- `EnsureMeterOrderEntry` inserts missing meter keys; if `afterKey` is absent or invalid, appends to end.

### Constants
- `Constants.Display` — bar geometry for the CPU panel. All UI bar sizing comes from here; no inline numbers in `MainWindow`.
- `Constants.Hardware` — hard caps on counters. Changing `MaxLogicalCoreCounters` or `MaxGpuAdapters` only affects how many per-instance counters are spun up, never aggregate values.
- `Constants.Timing` — update intervals and cache TTLs. All timer intervals and cache validity windows come from here.
- `Constants.Files` — filenames for settings and backup.
- `Constants.Process` — single-instance mutex name and fatal-error threshold.
- `Constants.Hotkey` — hotkey ID and virtual key.

## Work Guidance

### Adding a new settings field
1. Add to the appropriate nested class in `AppSettings` with a sensible default.
2. If the field needs migration from old JSON, add a `Has(rawJson, "FieldName")` check in `MigrateSettings`.
3. If the field is user-facing in SettingsWindow, add the corresponding UI control.

### Adding a new constant
- If it is a timing/interval value → `Constants.Timing`.
- If it is a display/geometry value → `Constants.Display`.
- If it is a hardware counter limit → `Constants.Hardware`.
- If it is a Win32 identifier (WM_*, SWP_*, etc.) → put it next to the `DllImport` in `NativeMethods.cs`, not here.

## Verification

`dotnet test Tests/WinMeters.Tests.csproj` — `AppSettingsTests`, `JsonMigrationTests`.

## Child DOX Index

<!-- No child AGENTS.md files -->