# Rendering Policy — WinMeters

WinMeters renders the bar and the configuration dialog with **only WPF and GDI+**. No third-party rendering library, no Win32 paint API, no other stack.

## What we render with

| Subsystem | Technology | Files |
|---|---|---|
| Main bar chrome (text, panels, separators, layout, hit-test) | **WPF** (`System.Windows.Controls.*`, `Shapes.*`) | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| CPU bars (per-core height / system vs. user fill) | **WPF** (`Rectangle` inside `Grid`) | `MainWindow.xaml`, `MainWindow.xaml.cs::UpdateCpuMeters` |
| RAM / VRAM / SRAM pies | **GDI+** (`System.Drawing.Graphics.FillPie` / `DrawEllipse`) into a **WPF `WriteableBitmap` backbuffer** | `Utils/PieChartRenderer.cs` |
| Settings dialog chrome | **WPF** (`System.Windows.Controls.Window`, `Controls.*`) | `SettingsWindow.xaml`, `SettingsWindow.xaml.cs` |
| Color picker dialog | **GDI+ / WinForms** (`System.Windows.Forms.ColorDialog`) | `ColorHelper.cs`, `SettingsWindow.xaml.cs` (click-handler) |

The pies are the only place where GDI+ drives a paint surface that is then hosted in WPF. The technique is: allocate a `WriteableBitmap(width × dpiBucket, height × dpiBucket, 96, 96, PixelFormats.Bgra32, null)`, lock the backbuffer, build a `System.Drawing.Bitmap(width, height, stride, PixelFormat.Format32bppArgb, backbuffer)` that wraps the **same** pixels, draw with `Graphics.FromImage(bitmap)`, unlock, mark the rect dirty. Result: GDI+ draws directly into WPF-native memory with **zero managed allocations per re-render** and **zero unmanaged handles** to release.

## What we deliberately do **not** use

- `UpdateLayeredWindow` — WPF `Window.AllowsTransparency="True"` owns the paint.
- `BitBlt`, `StretchDIBits`, `SetDIBitsToDevice`, `AlphaBlend` — anything involving `Graphics.FromHwnd(IntPtr)`.
- `Bitmap.GetHbitmap` / `Imaging.CreateBitmapSourceFromHBitmap` — replaced by the `WriteableBitmap` backbuffer-share trick above to avoid HBITMAP handles entirely.
- SharpDX, SkiaSharp, OpenTK, Vortice, OpenGL, SDL2, Direct2D, Direct3D — no third-party rendering packages, transitively or otherwise.
- WinForms `Control.CreateGraphics` — the only WinForms surface is the `ColorDialog` modal picker.
- `RenderTargetBitmap`-into-HWND composites.
- `System.Drawing.Printing` or `Metafile` emf/wmf rendering.

## Non-rendering Win32 interop (positioning only)

`NativeMethods.cs` and `Services/AppBarService.cs` use user32 / shell32 / dwmapi / shcore to position the bar in the taskbar, query per-monitor DPI, register hotkeys, and re-assert TOPMOST. These calls are shell positioning signals and never paint pixels. They are part of the codebase for placement, not for rendering.

## Pixel-grid alignment

The pies pixel-size is `LogicalSize × DpiBucket × Scale`, where `DpiBucket` is one of `{1.00, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00}` matched against `DpiScale = GetDpiForWindow(hwnd)/96.0`. The XAML declares each `<Image>` at `LogicalSize × LogicalSize` DIPs with `Stretch="Uniform"`, so the bitmap always lands on the monitor's pixel grid. This matches kil0bit's behaviour of sizing the bitmap to `(ShowPods ? 36 : 32) × DPI × Scale` and is why we cache by both `percentage` and `dpiBucket`.

## Anti-aliasing

The pies render with `Graphics.SmoothingMode = AntiAlias` and `PixelOffsetMode = HighQuality`. At 24×24 base size the AA-softened edge marries perfectly with WPF's MSAA-equivalent compositor; at 200 % DPI the upsampled 48×48 source still reads sharp because every monitor-pixel has at least one source pixel.

## Adding new visuals — checklist

1. **Will WPF draw it?** Use `Path`, `Ellipse`, `Rectangle`, `Shape`, or `TextBlock` first.
2. **Will the shape be a flat raster with cheap redraw cost (e.g. an icon, a pie, a sparkline)?** Use the GDI+-into-`WriteableBitmap` pattern from `PieChartRenderer.cs`. Reuse a single `WriteableBitmap` per visual; only re-draw when the underlying data changes.
3. **Will it need DirectX-only features?** Don't — express it in WPF / GDI+ or contribute it as a new pie / shape in `PieChartRenderer.cs` first.
4. **Will it manipulate colours?** Use `ColorHelper.ParseBrush` (WPF) for UI bindings, `ColorHelper.ToDrawingColor` for GDI+ paths.

## Verification

- `dotnet build WinMeters.csproj -nologo` must report 0 errors / 0 warnings.
- `dotnet test Tests/WinMeters.Tests.csproj -nologo` must report all tests passing. Tests are pure; the only STA requirement is for tests that construct a `new Image()` (via `RunOnStaThread` in `PieChartRendererTests.cs`).
- `git diff v0.0.1..HEAD -- RENDERING.md` shows this document plus any rendering change that triggered an edit here.

## History

- **v0.0.1** (commit `e48ca6d`, tag `v0.0.1`): Baseline state — pies already WPF (`PathGeometry`); no third-party rendering libs. Compliance with this policy: yes.
- **next**: GDI+ pie renderer via `WriteableBitmap` backbuffer (commit on `refactor/gdi-plus-pie-rendering`). Compliance still: yes (now using both halves of the WPF + GDI+ stack).
