using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
// `PixelFormats` lives in `System.Windows.Media` (not `.Imaging`). Importing that
// namespace would clash with `System.Drawing.Color`; the alias below keeps the
// `PixelFormats.Bgra32` usage in `CreatePieBitmap` resolvable without re-introducing
// the Color ambiguity.
using PixelFormats = System.Windows.Media.PixelFormats;
// Disambiguate the WPF Image control from System.Drawing.Image. The implicit-usings in
// the test csproj bring in System.IO plus both namespaces aliased here will continue to
// be available; qualifying the WPF alias inline keeps the file sync-friendly across the
// WinMeters + Tests build.
using WpfImage = System.Windows.Controls.Image;

namespace WinMeters.Utils;

/// <summary>
/// Pure-math + GDI+ rendering helpers for the RAM / VRAM / SRAM pie meters.
/// Draws pies with <see cref="System.Drawing.Graphics"/> into a WPF
/// <see cref="WriteableBitmap"/> backbuffer, then hosts the resulting
/// <see cref="BitmapSource"/> in a <see cref="WpfImage"/>.
/// </summary>
/// <remarks>
/// <para>
/// The renderer is intentionally minimal:
/// <list type="bullet">
/// <item><see cref="UpdatePieWithCache"/> reuses the cached <see cref="BitmapSource"/>
/// when both the percentage drift and the DPI bucket are unchanged, avoiding any
/// per-tick allocation when the data is stable.</item>
/// <item>DPI bucketing keeps the bitmap crisp at 150 / 200 / 250 % scalings — the
/// resulting <see cref="WriteableBitmap"/> is <see cref="LogicalSize"/> × <c>DpiMultiplier</c>
/// physical pixels and is displayed at <see cref="LogicalSize"/> × <see cref="LogicalSize"/>
/// DIPs via the WPF <c>Image</c> (<c>Stretch="Uniform"</c> downsamples when needed).</item>
/// </list>
/// </para>
/// <para>
/// All options are explicit parameters so this class does not have to know about
/// <see cref="AppSettings"/>; the caller supplies thickness and colours from its own
/// settings model.
/// </para>
/// </remarks>
internal static class PieChartRenderer
{
    /// <summary>Percent-delta below which the cached bitmap is reused.</summary>
    public const double CacheThresholdPercent = 0.1;

    /// <summary>
    /// Logical (DIP) edge length of a pie chart. Matches the WPF Image
    /// <c>Width</c> / <c>Height</c> in <c>MainWindow.xaml</c>. Pixel size = this × multi.
    /// </summary>
    public const double LogicalSize = 24;

    /// <summary>
    /// 0.25-step DPI buckets used as the cache key alongside percentage.
    /// Each entry is the multiplier applied to <see cref="LogicalSize"/> when building
    /// the actual <see cref="WriteableBitmap"/> pixel dimensions. Indexed by
    /// <see cref="DpiBucketFor(double)"/>.
    /// </summary>
    private static readonly double[] DpiMultipliers = { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

    /// <summary>
    /// Maps a per-monitor DPI scale (e.g. <c>1.5</c> for 150 %) to an integer bucket
    /// index into <see cref="DpiMultipliers"/>. Out-of-range values are clamped:
    /// sub-100 % scales snap to the 100 % bucket and >300 % scales snap to the 300 %
    /// bucket. The mapping is <c>(dpiScale − 1) × 4</c>, rounded to nearest integer,
    /// so the bucket table reads left-to-right as 100 / 125 / 150 / 175 / 200 / 250 / 300 %.
    /// </summary>
    public static int DpiBucketFor(double dpiScale)
    {
        int idx = (int)Math.Round((dpiScale - 1.0) * 4);
        return Math.Clamp(idx, 0, DpiMultipliers.Length - 1);
    }

    /// <summary>
    /// Updates <paramref name="image"/> to show the pie wedge for
    /// <paramref name="percentage"/> (0–100), with a stroke of
    /// <paramref name="borderThickness"/> DIPs in <paramref name="borderColor"/>
    /// around the wedge in <paramref name="fillColor"/>. Reuses the cached
    /// <see cref="BitmapSource"/> when both percentage and
    /// <paramref name="dpiScale"/> are stable within their respective tolerances.
    /// </summary>
    /// <param name="image">WPF image element whose <c>Source</c> is updated to the new bitmap.</param>
    /// <param name="percentage">0–100; values outside the range are clamped.</param>
    /// <param name="borderThickness">DIP thickness of the stroke; 0 omits the stroke.</param>
    /// <param name="fillColor">Wedge fill colour.</param>
    /// <param name="borderColor">Stroke colour.</param>
    /// <param name="dpiScale">Per-monitor DPI scale; bucketed by <see cref="DpiBucketFor"/>.</param>
    /// <param name="cachedSource">Caller-owned cache slot for the rendered bitmap; mutated on rebuild.</param>
    /// <param name="cachedPercentage">Caller-owned cache slot for the last percentage; mutated on rebuild.</param>
    /// <param name="cachedDpiBucket">Caller-owned cache slot for the last DPI bucket; mutated on rebuild.</param>
    public static void UpdatePieWithCache(
        WpfImage image,
        double percentage,
        double borderThickness,
        Color fillColor,
        Color borderColor,
        double dpiScale,
        ref BitmapSource? cachedSource,
        ref double cachedPercentage,
        ref int cachedDpiBucket)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));

        int bucket = DpiBucketFor(dpiScale);
        if (cachedSource is not null
            && bucket == cachedDpiBucket
            && Math.Abs(percentage - cachedPercentage) < CacheThresholdPercent)
        {
            image.Source = cachedSource;
            return;
        }

        cachedPercentage = percentage;
        cachedDpiBucket = bucket;
        cachedSource = CreatePieBitmap(percentage, borderThickness, fillColor, borderColor, bucket);
        image.Source = cachedSource;
    }

    /// <summary>
    /// Builds a frozen-free <see cref="WriteableBitmap"/> for the given percentage,
    /// thickness, and colours. Stateless; safe to call outside a WPF dispatcher thread.
    /// </summary>
    public static BitmapSource CreatePieBitmap(
        double percentage,
        double borderThickness,
        Color fillColor,
        Color borderColor,
        int dpiBucket)
    {
        int pixelSize = (int)Math.Round(LogicalSize * DpiMultipliers[dpiBucket]);
        if (pixelSize < 1) pixelSize = 1;

        var writeable = new WriteableBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Bgra32, null);

        writeable.Lock();
        try
        {
            // The System.Drawing.Bitmap wraps the same pointer the WPF WriteableBitmap
            // owns. Disposing the Bitmap does not free the underlying memory; that is the
            // WriteableBitmap's job — Dispose is only releasing GDI-side bookkeeping.
            using var bmp = new Bitmap(
                pixelSize, pixelSize,
                writeable.BackBufferStride,
                PixelFormat.Format32bppArgb,
                writeable.BackBuffer);

            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            double pct = Math.Clamp(percentage, 0, 100);
            float cx = pixelSize / 2f;
            float cy = pixelSize / 2f;
            float radius = cx - (float)borderThickness / 2f;

            // Wedge (or full disc). Drawing the fill before the border hides the harsh
            // mathematical edge of the arc behind the stroke, matching the XAML layout
            // where the <Path> sits under the <Ellipse> border.
            if (radius > 0)
            {
                if (pct >= 100)
                {
                    using var fillBrush = new SolidBrush(fillColor);
                    g.FillEllipse(fillBrush, cx - radius, cy - radius, 2 * radius, 2 * radius);
                }
                else if (pct > 0)
                {
                    using var fillBrush = new SolidBrush(fillColor);
                    // Start at -90° (12 o'clock) and sweep clockwise by pct*3.6°.
                    g.FillPie(fillBrush, cx - radius, cy - radius, 2 * radius, 2 * radius, -90f, (float)(pct * 3.6));
                }
            }

            if (borderThickness > 0 && radius > 0)
            {
                using var borderPen = new Pen(borderColor, (float)borderThickness);
                g.DrawEllipse(borderPen, cx - radius, cy - radius, 2 * radius, 2 * radius);
            }
        }
        finally
        {
            writeable.AddDirtyRect(new Int32Rect(0, 0, pixelSize, pixelSize));
            writeable.Unlock();
        }

        return writeable;
    }
}
