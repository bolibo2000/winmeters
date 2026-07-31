# Tests Domain

## Purpose
Unit and integration tests for WinMeters. Tests live in `Tests/` and share the same solution as the main project.

## Ownership
Owns `Tests/*.cs`.

## Local Contracts

### What to test
- **ColorHelper** — hex parsing roundtrip, null/empty fallback, cache hit on repeated color.
- **AppSettings** — JSON roundtrip, migration from older settings versions, default values.
- **PieChartRenderer** — DPI bucket mapping, bitmap output dimensions, cache invalidation.
- **JsonMigration** — malformed JSON, missing fields, legacy field → new field mapping.

### What NOT to test
- `Log` — writes to `%LOCALAPPDATA%`; testing it requires mocking file I/O.
- `MonitorManager` / `HardwareMonitorService` — require live performance counters; tested via manual/integration runs only.
- WPF bindings and XAML — tested manually.

### Test naming
Use `Method_Scenario_ExpectedResult` (xUnit style). Example: `DpiBucketFor_MapsScalesToBucketIndices`.

## Work Guidance

### Running tests
```bash
dotnet test Tests/WinMeters.Tests.csproj -c Debug
```

### Adding a new test class
- Follow `WinMeters.Tests.csproj` multi-target framework — tests run on `net10.0-windows`.
- Use `[Theory]` + `[InlineData]` for parameterized tests.
- Mock types from the main project via `InternalsVisibleTo`.

## Verification

`dotnet test Tests/WinMeters.Tests.csproj` — 71 tests (68 pass, 3 pre-existing failures in `JsonMigrationTests` and `PieChartRendererTests` as of 2026-07-19).

## Child DOX Index

<!-- No child AGENTS.md files in Tests/ -->