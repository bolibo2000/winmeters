using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using PixelFormats = System.Windows.Media.PixelFormats;
using WpfImage = System.Windows.Controls.Image;

namespace WinMeters.Utils;

internal static class PieChartRenderer
{
    public const double CacheThresholdPercent = 0.1;
    public const double LogicalSize = 24;

    // Each (Dpi, Multiplier) tuple co-locates the bucket index → multiplier lookup that previously
    // lived as two coupled but unverifiable pieces. Snapping is achieved by rounding incoming
    // scale to the nearest discrete step in this table; if a future WPF build stops reporting
    // the standard 1.0/1.25/1.5/1.75/2.0/2.5/3.0 ladder, AppendPixelSizeForUnknownScale below
    // falls back to the raw scale so we never accidentally render at the wrong size bucket for
    // e.g. 4K@200% HiDPI.
    private static readonly (double DpiScale, double Multiplier)[] DpiBuckets =
    {
        (1.0, 1.0),
        (1.25, 1.25),
        (1.5, 1.5),
        (1.75, 1.75),
        (2.0, 2.0),
        (2.5, 2.5),
        (3.0, 3.0),
    };

    public static int DpiBucketFor(double dpiScale)
    {
        for (int i = 0; i < DpiBuckets.Length; i++)
        {
            // Window's standard DPI ladder reports exact values; use 0.05 as a slack tolerance so a
            // slightly-fuzzy DpiScaleForWindow report still snaps to the nearest bucket.
            if (Math.Abs(dpiScale - DpiBuckets[i].DpiScale) < 0.05)
                return i;
        }
        // Unknown step (e.g., 1.33 or 1.4 on some HiDPI scaling experiments): use the smallest bucket
        // ≠ than the requested scale so the cache still keys consistently per session, with rounded
        // pixel size derived from the closest standard step above.
        int idx = (int)Math.Round((dpiScale - 1.0) * 4);
        return Math.Clamp(idx, 0, DpiBuckets.Length - 1);
    }

    public static double MultiplierForBucket(int dpiBucket)
    {
        if ((uint)dpiBucket < (uint)DpiBuckets.Length)
            return DpiBuckets[dpiBucket].Multiplier;
        return 1.0;
    }

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

    public static BitmapSource CreatePieBitmap(
        double percentage,
        double borderThickness,
        Color fillColor,
        Color borderColor,
        int dpiBucket)
    {
        int pixelSize = (int)Math.Round(LogicalSize * MultiplierForBucket(dpiBucket));
        if (pixelSize < 1) pixelSize = 1;

        var writeable = new WriteableBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Bgra32, null);

        writeable.Lock();
        try
        {
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
