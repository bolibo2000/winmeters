# Services Domain

## Purpose
Win32 integration layer: window docking, global hotkeys, right-click popup menus, dark-mode theming, and window position persistence.

## Ownership
Owns all `Services/*.cs`.

## Local Contracts

### Thread safety
- `HotkeyService`, `AppBarService`, `BarPopupMenuService` are single-instance per window; do not register multiple instances for the same HWND.
- `WindowPlacementService` reads window position; call only after `OnSourceInitialized` when the handle is valid.

### Win32 error handling
- Win32 API failures (RegisterHotKey, SHAppBarMessage, SetWindowLongPtr) log and continue — they never throw. The feature degrades gracefully (e.g., no taskbar dock, no hotkey).
- `EntryPointNotFoundException` on `SetPreferredAppMode` / `AllowDarkModeForWindow` / `FlushMenuThemes` is caught and treated as a fallback (force dark) — this catches older Windows 10 pre-1903 builds.

### Dark mode
- `ThemeService.InitializeDarkMode()` is called once in `App.OnStartup` before any window opens, so all windows inherit the mode. Subsequent calls in window constructors are redundant and must not be added.
- `BarPopupMenuService.ApplyMenuChromeMode` re-applies `SetPreferredAppMode` based on the current system theme (dark→FORCE_DARK, light→DEFAULT) before calling `AllowDarkModeForWindow`, so runtime theme switches are reflected on next menu open.

### Mutex lifetime
- `App.SingleInstanceMutex` (named `"WinMeters_SingleInstance_Mutex"`) is acquired in `OnStartup` and released in both `ReleaseSingleInstanceMutex()` and `OnExit` via `DisposeSingleInstanceMutex`. Idempotent against null.

### AppBar docking
- When `StickToTaskbar=true`, the window is re-parented to `Shell_TrayWnd` and DWM transitions are forced off via `DWMWA_TRANSITIONS_FORCEDISABLED`. DPI is cached on attach and refreshed on `WM_DPICHANGED`.

## Work Guidance

### Adding a new Win32 P/Invoke
1. Add the `DllImport` to `NativeMethods.cs` in the appropriate section (window, cursor, menu, etc.).
2. Add `const` values for numeric identifiers (WM_*, TPM_*, etc.) near the declaration or in a nested struct.
3. Do not re-declare Win32 constants in individual service files.

### Adding a new service
- Create in `Services/`.
- Throw `ArgumentNullException` on null constructor params.
- Implement `IDisposable` if it holds Win32 resources; call `Dispose()` from the window's close handler.
- Do not call `ThemeService` from a service constructor — dark mode is already set by then.

## Verification

`dotnet test Tests/WinMeters.Tests.csproj` — no domain-specific integration tests yet.

## Child DOX Index

<!-- No child AGENTS.md files in Services/ -->