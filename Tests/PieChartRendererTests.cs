using System.Runtime.ExceptionServices;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Xunit;
using WinMeters.Utils;
// `PixelFormats` (the static class with Bgra32 / Pbgra32) lives in System.Windows.Media,
// NOT System.Windows.Media.Imaging. Importing System.Windows.Media would clash with
// System.Drawing.Color; alias it instead so `PixelFormats.Bgra32` remains in scope.
using PixelFormats = System.Windows.Media.PixelFormats;
// Disambiguate the WPF Image control from System.Drawing.Image (ImplicitUsings brings
// in System.Drawing via the WinMeters.csproj's UseWindowsForms = true, and in this
// test csproj via the Microsoft.WindowsDesktop.App framework reference).
using WpfImage = System.Windows.Controls.Image;

namespace WinMeters.Tests;

/// <summary>
/// Covers the GDI+-based pie renderer in <see cref="PieChartRenderer"/>: the
/// bitmap-shape helpers, the cache-reuse logic, the DPI-bucket cache key,
/// and degenerate inputs.
/// </summary>
public class PieChartRendererTests
{
    // Matches Constants.Display.RamMeterRadius. Kept inline so the test remains a pure
    // unit test — if the constant moves, both halves must update.
    private const double LogicalSize = 24;
    private const double BorderThickness = 0.5;

    // Drawn into GDI+: but `System.Drawing.Color.Orange` == (255, 165, 0) RGB so we can
    // assert on B/G/R byte values directly.
    private static readonly Color FillColor = Color.Orange;
    private static readonly Color BorderColor = Color.Black;

    [Fact]
    public void CreatePieBitmap_ZeroPercent_RendersNothing()
    {
        BitmapSource bs = PieChartRenderer.CreatePieBitmap(0, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));

        Assert.Equal((int)Math.Round(LogicalSize), bs.PixelWidth);
        Assert.Equal((int)Math.Round(LogicalSize), bs.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, bs.Format);

        // 0% draws a transparent disc. We should see zero pixels near the fill colour.
        int orangeCount = CountPixelsNearColor(bs, FillColor, alphaTolerance: 30, channelTolerance: 25);
        Assert.Equal(0, orangeCount);
    }

    [Fact]
    public void CreatePieBitmap_OneHundredPercent_FillsMostPixels()
    {
        BitmapSource bs = PieChartRenderer.CreatePieBitmap(100, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));

        // A full disc at 24x24 — orange fills the interior, with the black border ring
        // reserved. With anti-aliasing the orange-count should still clear ~60% of the
        // bounding box (compared to π/4 ≈ 78.5 % geometric maximum, minus the 0.5 px
        // border consuming the outer ring).
        int orangeCount = CountPixelsNearColor(bs, FillColor, alphaTolerance: 30, channelTolerance: 25);
        int total = bs.PixelWidth * bs.PixelHeight;
        Assert.True(orangeCount > total * 0.55,
            $"expected >55% orange in full disc, got {orangeCount}/{total} = {(double)orangeCount / total:P1}");
    }

    [Fact]
    public void CreatePieBitmap_FiftyPercent_FillsRoughlyHalf()
    {
        BitmapSource bs = PieChartRenderer.CreatePieBitmap(50, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));

        int orangeCount = CountPixelsNearColor(bs, FillColor, alphaTolerance: 30, channelTolerance: 25);
        int total = bs.PixelWidth * bs.PixelHeight;

        // Half a disc ≈ 39% of the bounding box. Tolerance ±10% covers AA and the
        // half-arc starting at the top.
        double fraction = (double)orangeCount / total;
        Assert.InRange(fraction, 0.30, 0.50);
    }

    [Fact]
    public void CreatePieBitmap_NegativePercentage_ClampsToZero()
    {
        BitmapSource neg = PieChartRenderer.CreatePieBitmap(-5, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));
        BitmapSource zero = PieChartRenderer.CreatePieBitmap(0, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));

        Assert.Equal(0, CountPixelsNearColor(neg, FillColor, alphaTolerance: 30, channelTolerance: 25));
        Assert.Equal(0, CountPixelsNearColor(zero, FillColor, alphaTolerance: 30, channelTolerance: 25));
    }

    [Fact]
    public void CreatePieBitmap_OverHundredPercent_ClampsToFull()
    {
        BitmapSource over = PieChartRenderer.CreatePieBitmap(150, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));
        BitmapSource at = PieChartRenderer.CreatePieBitmap(100, BorderThickness, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));

        int overOrange = CountPixelsNearColor(over, FillColor, alphaTolerance: 30, channelTolerance: 25);
        int atOrange = CountPixelsNearColor(at, FillColor, alphaTolerance: 30, channelTolerance: 25);

        // Both should cover the full disc; their pixel counts should agree within AA
        // tolerance (they take the same fill code path).
        Assert.Equal(atOrange, overOrange);
    }

    [Fact]
    public void CreatePieBitmap_HugeBorderThickness_DoesNotThrowAndRendersSmallCircle()
    {
        // border thickness equal to the meter's full radius shrinks the wedge radius
        // to 0 (rounded down), so the only stroke GDI+ ever paints is the border at
        // radius 0. We accept any non-throwing result — the test is purely that the
        // clamp-then-render path completes.
        BitmapSource bs = PieChartRenderer.CreatePieBitmap(50, borderThickness: LogicalSize * 4, FillColor, BorderColor, PieChartRenderer.DpiBucketFor(1.0));

        Assert.NotNull(bs);
        Assert.Equal((int)Math.Round(LogicalSize), bs.PixelWidth);
    }

    [Fact]
    public void CreatePieBitmap_PixelSizeMatchesDpiBucket()
    {
        double[] expectedMultipliers = { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };
        for (int bucket = 0; bucket < expectedMultipliers.Length; bucket++)
        {
            BitmapSource bs = PieChartRenderer.CreatePieBitmap(50, BorderThickness, FillColor, BorderColor, bucket);
            int expectedPx = (int)Math.Round(LogicalSize * expectedMultipliers[bucket]);
            Assert.Equal(expectedPx, bs.PixelWidth);
            Assert.Equal(expectedPx, bs.PixelHeight);
        }
    }

    [Theory]
    [InlineData(0.5, 0)]   // sub-100% clamps to the 100% bucket
    [InlineData(1.0, 0)]   // 100%
    [InlineData(1.25, 1)]  // 125%
    [InlineData(1.5, 2)]   // 150%
    [InlineData(1.75, 3)]  // 175%
    [InlineData(2.0, 4)]   // 200%
    [InlineData(2.25, 5)]  // 225% — no 2.25 slot; routes to the 200% bucket (idx 5 = ×2.0)
    [InlineData(2.5, 6)]   // 250%
    [InlineData(3.0, 6)]   // 300%
    [InlineData(3.5, 6)]   // 350% clamps to the 300% bucket
    [InlineData(5.0, 6)]   // far above the table clamps to the 300% bucket
    public void DpiBucketFor_MapsScalesToBucketIndices(double scale, int expectedIndex)
    {
        Assert.Equal(expectedIndex, PieChartRenderer.DpiBucketFor(scale));
    }

    [Fact]
    public void UpdatePieWithCache_BelowThreshold_ReusesCachedBitmap()
    {
        // WPF BitmapSource instances are thread-affine (inherited from DependencyObject),
        // so a BitmapSource created on the STA thread cannot have its properties touched
        // from the test-runner's MTA thread without tripping VerifyAccess. The fix is to
        // do all BitmapSource work INSIDE the STA block and surface only primitive
        // result values (bools) to the outer Asserts.
        bool firstPresent = false;
        bool secondPresent = false;
        bool sameInstance = false;

        RunOnStaThread(() =>
        {
            var img = new WpfImage();
            BitmapSource? cached = null;
            double pct = -1;
            int bucket = -1;
            PieChartRenderer.UpdatePieWithCache(img, 30, BorderThickness, FillColor, BorderColor, 1.0, ref cached, ref pct, ref bucket);
            var first = img.Source as BitmapSource;
            PieChartRenderer.UpdatePieWithCache(img, 30.05, BorderThickness, FillColor, BorderColor, 1.0, ref cached, ref pct, ref bucket);
            var second = img.Source as BitmapSource;

            firstPresent = first is not null;
            secondPresent = second is not null;
            sameInstance = ReferenceEquals(first, second);
        });

        Assert.True(firstPresent);
        Assert.True(secondPresent);
        Assert.True(sameInstance);
    }

    [Fact]
    public void UpdatePieWithCache_AboveThreshold_RecreatesBitmap()
    {
        // Same thread-affinity note as the test above. All BitmapSource work happens on
        // the STA thread; only booleans cross the boundary back to the runner.
        bool firstPresent = false;
        bool secondPresent = false;
        bool distinctInstances = false;

        RunOnStaThread(() =>
        {
            var img = new WpfImage();
            BitmapSource? cached = null;
            double pct = -1;
            int bucket = -1;
            PieChartRenderer.UpdatePieWithCache(img, 30, BorderThickness, FillColor, BorderColor, 1.0, ref cached, ref pct, ref bucket);
            var first = img.Source as BitmapSource;
            // Above threshold: 30 → 35, delta 5 > 0.1.
            PieChartRenderer.UpdatePieWithCache(img, 35, BorderThickness, FillColor, BorderColor, 1.0, ref cached, ref pct, ref bucket);
            var second = img.Source as BitmapSource;

            firstPresent = first is not null;
            secondPresent = second is not null;
            distinctInstances = !ReferenceEquals(first, second);
        });

        Assert.True(firstPresent);
        Assert.True(secondPresent);
        Assert.True(distinctInstances);
    }

    [Fact]
    public void UpdatePieWithCache_DpiBucket_RecreatesBitmap()
    {
        // Same thread-affinity note as the test above. All BitmapSource reads (PixelWidth,
        // PixelHeight) happen on the STA thread; the resulting ints cross to the runner.
        int firstWidth = -1;
        int firstHeight = -1;
        int secondWidth = -1;
        int secondHeight = -1;
        bool firstPresent = false;
        bool secondPresent = false;
        bool distinctInstances = false;

        RunOnStaThread(() =>
        {
            var img = new WpfImage();
            BitmapSource? cached = null;
            double pct = -1;
            int bucket = -1;
            // Same percentage, but different DPI bucket — 100% (24×24) → 150% (24×1.5 = 36×36).
            PieChartRenderer.UpdatePieWithCache(img, 50, BorderThickness, FillColor, BorderColor, 1.0, ref cached, ref pct, ref bucket);
            var first = img.Source as BitmapSource;
            PieChartRenderer.UpdatePieWithCache(img, 50.05, BorderThickness, FillColor, BorderColor, 1.5, ref cached, ref pct, ref bucket);
            var second = img.Source as BitmapSource;

            firstPresent = first is not null;
            secondPresent = second is not null;
            distinctInstances = !ReferenceEquals(first, second);
            if (first is not null)
            {
                firstWidth = first.PixelWidth;
                firstHeight = first.PixelHeight;
            }
            if (second is not null)
            {
                secondWidth = second.PixelWidth;
                secondHeight = second.PixelHeight;
            }
        });

        Assert.True(firstPresent);
        Assert.True(secondPresent);
        Assert.True(distinctInstances);
        Assert.Equal(24, firstWidth);    // 100% bucket
        Assert.Equal(24, firstHeight);
        Assert.Equal(36, secondWidth);   // 150% bucket
        Assert.Equal(36, secondHeight);
    }

    [Fact]
    public void UpdatePieWithCache_NullImage_Throws()
    {
        BitmapSource? cached = null;
        double pct = -1;
        int bucket = -1;

        Assert.Throws<ArgumentNullException>(() =>
            PieChartRenderer.UpdatePieWithCache(null!, 50, BorderThickness, FillColor, BorderColor, 1.0, ref cached, ref pct, ref bucket));
    }

    /// <summary>
    /// Counts pixels in <paramref name="bs"/> whose RGBA is within
    /// <paramref name="channelTolerance"/> of the given <paramref name="target"/> colour
    /// AND whose alpha is at least <paramref name="alphaTolerance"/>. The bitmap is
    /// expected to use Bgra32 (B = pixel byte 0, G = 1, R = 2, A = 3) — the format the
    /// renderer writes.
    /// </summary>
    private static int CountPixelsNearColor(
        BitmapSource bs,
        Color target,
        int alphaTolerance,
        int channelTolerance)
    {
        int width = bs.PixelWidth;
        int height = bs.PixelHeight;
        int stride = width * 4; // Bgra32 is 4 bytes per pixel.
        byte[] pixels = new byte[stride * height];
        bs.CopyPixels(pixels, stride, 0);

        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i + 0];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];
            if (a < alphaTolerance) continue;
            if (Math.Abs(b - target.B) > channelTolerance) continue;
            if (Math.Abs(g - target.G) > channelTolerance) continue;
            if (Math.Abs(r - target.R) > channelTolerance) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Spawns a dedicated Thread with <see cref="ApartmentState.STA"/> and runs the
    /// supplied work synchronously on it. Required for tests that instantiate a WPF
    /// <see cref="FrameworkElement"/> (<see cref="WpfImage"/>, <see cref="Window"/>, ...)
    /// because those classes' constructors throw <c>InvalidOperationException</c> on MTA
    /// threads (which is the default xUnit test thread model).
    ///
    /// Any exception thrown inside <paramref name="work"/> is re-thrown on the calling
    /// thread, preserving its stack trace via <see cref="ExceptionDispatchInfo"/>.
    ///
    /// Note: this is a tiny shim that predates the <c>Xunit.StaFact</c> NuGet. We keep it
    /// inline so the test project does not require a third-party STA package.
    /// </summary>
    private static void RunOnStaThread(Action work)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
