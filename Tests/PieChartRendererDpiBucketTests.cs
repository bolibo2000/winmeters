using Xunit;

namespace WinMeters.Tests;

/// <summary>
/// Unit tests for the refactored DPI bucket helpers in <see cref="Utils.PieChartRenderer"/>.
/// <para>
/// These tests pin the bucket boundaries so a future change to the snapshot table (deleting a
/// step, reordering) is caught before it ships a subtly-wrong pie size at a particular scale.
/// </para>
/// </summary>
public class PieChartRendererDpiBucketTests
{
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(1.25, 1)]
    [InlineData(1.5, 2)]
    [InlineData(1.75, 3)]
    [InlineData(2.0, 4)]
    [InlineData(2.5, 5)]
    [InlineData(3.0, 6)]
    public void DpiBucketFor_StandardLadder_RoundTripsExactly(double dpi, int expectedBucket)
    {
        int bucket = Utils.PieChartRenderer.DpiBucketFor(dpi);

        Assert.Equal(expectedBucket, bucket);
    }

    [Theory]
    [InlineData(1.04, 0)]   // sub-tolerance of 1.0, snaps down via the 0.05 slack in DpiBucketFor.
    [InlineData(1.06, 0)]   // outside the 0.05 slack window → falls through to the (dpiScale-1.0)*4 formula which yields bucket 0 (1.06 is closer to 1.0 than to 1.25).
    [InlineData(1.92, 4)]   // rounds to bucket 4 via the (dpiScale-1.0)*4 formula.
    [InlineData(2.7, 6)]    // formula yields 6.8 → 7 → clamped to 6 → bucket 6 (300% bucket).
    public void DpiBucketFor_NonLadderValues_SnapsToNearestOrWithinTolerance(double dpi, int expectedBucket)
    {
        int bucket = Utils.PieChartRenderer.DpiBucketFor(dpi);

        Assert.Equal(expectedBucket, bucket);
    }

    [Fact]
    public void MultiplierForBucket_ReturnsCanonicalMultipliers()
    {
        // The co-located (Dpi,Multiplier) table means bucket → multiplier is no longer an
        // indexIntoDpiMultipliers race. Pin the table so a re-tune goes through this test first.
        Assert.Equal(1.0, Utils.PieChartRenderer.MultiplierForBucket(0));
        Assert.Equal(1.25, Utils.PieChartRenderer.MultiplierForBucket(1));
        Assert.Equal(1.5, Utils.PieChartRenderer.MultiplierForBucket(2));
        Assert.Equal(1.75, Utils.PieChartRenderer.MultiplierForBucket(3));
        Assert.Equal(2.0, Utils.PieChartRenderer.MultiplierForBucket(4));
        Assert.Equal(2.5, Utils.PieChartRenderer.MultiplierForBucket(5));
        Assert.Equal(3.0, Utils.PieChartRenderer.MultiplierForBucket(6));
    }

    [Fact]
    public void MultiplierForBucket_OutOfRange_FallsBackToOne()
    {
        // OOB bucket should never happen given ClampPercent, but we explicitly want a safe 1.0
        // fallback (the old Array `DpiMultipliers[idx]` would have thrown IndexOutOfRange).
        Assert.Equal(1.0, Utils.PieChartRenderer.MultiplierForBucket(-1));
        Assert.Equal(1.0, Utils.PieChartRenderer.MultiplierForBucket(99));
    }

    /// <summary>
    /// Property capture: ensure LogicalSize drives the bucketed pixel size exactly. A 24px logical
    /// chart at the 2.0 bucket must be 48 device pixels — these attempts to track a common
    /// off-by-one regression path where bucket indexes shift silently.
    /// </summary>
    [Theory]
    [InlineData(0, 24)]   // 24 * 1.0
    [InlineData(1, 30)]   // 24 * 1.25
    [InlineData(4, 48)]   // 24 * 2.0
    [InlineData(6, 72)]   // 24 * 3.0
    public void Bucket_Multiplier_ProducesExpectedPixelSize(int bucket, int expectedPixelSize)
    {
        double multiplier = Utils.PieChartRenderer.MultiplierForBucket(bucket);
        int pixelSize = (int)Math.Round(Utils.PieChartRenderer.LogicalSize * multiplier);

        Assert.Equal(expectedPixelSize, pixelSize);
    }
}
