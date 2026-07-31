# UI Domain

## Purpose
WPF windows, App bootstrap, and theming. Contains all `Window` subclasses, the shared `App` entry point, and the WinMeters theme resource dictionary.

## Ownership
Owns `App.xaml[.cs]`, `MainWindow.xaml[.cs]`, `SettingsWindow.xaml[.cs]`, `AboutWindow.xaml[.cs]`, `Themes/WinMetersTheme.xaml`.

## Local Contracts

### App startup
1. Acquire single-instance mutex. If already owned, show message and exit.
2. Call `ThemeService.InitializeDarkMode()` — **once**, before any window is created.
3. Load settings with `AppSettings.Load()`.
4. Initialize `MainWindow`.

### Window lifecycle
- `MainWindow` is the only window that lives for the entire app session. It owns the tray icon and all update timers.
- `SettingsWindow` and `AboutWindow` are modal-less dialogs with `Owner = MainWindow`. Only one instance of each is allowed (`_existingSettingsWindow` / `_existingAboutWindow`).
- All windows call `ThemeService.InitializeDarkMode()` is **not needed** — already done in `App.OnStartup`.

### Tray icon
- Created in `MainWindow` constructor; lives until `MainWindow_Closed` disposes it.
- Double-click toggles visibility (same as hotkey). Context menu has Settings, Show/Hide Bar, About, Quit.
- The `ToggleVisibilityItemTag = "ToggleVisibility"` string is the protocol marker shared between `BuildTrayMenu` (writer) and `RefreshTrayMenuToggleItem` (reader).

### Timer update flow
`Timer_Tick` → `UpdateCpuMeters` / `UpdateRamMeter` / `UpdateDiskMeter` / `UpdateNetMeter` / `UpdateHardwareSensors` / `UpdateGpuMemoryMeters` / `UpdateTime` → `UpdateTooltips`.
Each `Update*` method gates on its own `IsReadyToUpdate(ref lastTicks, rateMs, nowTicks)` to respect per-meter rates.
`ApplySettingsLive` propagates settings changes immediately without closing the Settings window.

### Theme resources
`Themes/WinMetersTheme.xaml` defines `ThemeBgBrush` and `ThemeTextBrush`. These are applied in each window's constructor after `InitializeComponent`.

## Work Guidance

### Adding a new meter panel
1. Add the XAML `Border`/`StackPanel` to `MainWindow.xaml` with a meaningful `x:Name`.
2. Add `Update*Meter` method in `MainWindow.xaml.cs` following the `IsReadyToUpdate` pattern.
3. Call it from `Timer_Tick`.
4. Add visibility toggle in `ApplyVisibility`.
5. Add to `ApplyMeterOrder`'s `map` dictionary if it participates in ordering.
6. Add to `UpdateTooltips` if it needs a tooltip.

### Adding a new window
- Place the `.xaml` and `.xaml.cs` at root level.
- Set `Owner = MainWindow` if it is a dialog.
- Call `ThemeService.InitializeDarkMode()` is **not needed**.
- Apply `ThemeBgBrush` / `ThemeTextBrush` from `ColorHelper.ThemeBrush`.

## Verification

`dotnet test Tests/WinMeters.Tests.csproj` — no UI integration tests; test the logic in domain classes instead.

## Child DOX Index

<!-- No child AGENTS.md files in UI/ -->