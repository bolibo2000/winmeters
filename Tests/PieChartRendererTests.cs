using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Xunit;
using WinMeters.Utils;
// Disambiguate the WPF Path from System.IO.Path (ImplicitUsings in this csproj brings
// in System.IO). Same convention as the alias in Utils/PieChartRenderer.cs.
using WpfPath = System.Windows.Shapes.Path;

namespace WinMeters.Tests;

/// <summary>
/// Covers the pure-math helpers in <see cref="PieChartRenderer"/>: the pie geometry,
/// the cache-reuse logic, and degenerate inputs.
/// </summary>
public class PieChartRendererTests
{
    private const double BorderThickness = 0.5;

    [Fact]
    public void CreatePieGeometry_ZeroPercent_ReturnsEmptyEllipse()
    {
        Geometry g = PieChartRenderer.CreatePieGeometry(0, BorderThickness);

        var ellipse = Assert.IsType<EllipseGeometry>(g);
        Assert.Equal(0, ellipse.RadiusX);
        Assert.Equal(0, ellipse.RadiusY);
    }

    [Fact]
    public void CreatePieGeometry_OneHundredPercent_ReturnsFullDisk()
    {
        Geometry g = PieChartRenderer.CreatePieGeometry(100, BorderThickness);

        var ellipse = Assert.IsType<EllipseGeometry>(g);
        double expectedRadius = Constants.Display.RamMeterRadius - BorderThickness / 2.0;
        Assert.Equal(expectedRadius, ellipse.RadiusX, precision: 6);
        Assert.Equal(expectedRadius, ellipse.RadiusY, precision: 6);
    }

    [Fact]
    public void CreatePieGeometry_FiftyPercent_ReturnsWedge()
    {
        Geometry g = PieChartRenderer.CreatePieGeometry(50, BorderThickness);

        var pathGeom = Assert.IsType<PathGeometry>(g);
        Assert.NotEmpty(pathGeom.Figures);
        // A normal 0% → 100% wedge is a PathGeometry; the ellipse cases above confirm
        // the boundary behaviour. Mid-range should always be a wedge.
    }

    [Fact]
    public void CreatePieGeometry_NegativePercentage_BoundaryTreatedAsZero()
    {
        Geometry neg = PieChartRenderer.CreatePieGeometry(-5, BorderThickness);
        Geometry zero = PieChartRenderer.CreatePieGeometry(0, BorderThickness);

        var negEllipse = Assert.IsType<EllipseGeometry>(neg);
        var zeroEllipse = Assert.IsType<EllipseGeometry>(zero);
        Assert.Equal(0, negEllipse.RadiusX);
        Assert.Equal(0, negEllipse.RadiusY);
        Assert.Equal(0, zeroEllipse.RadiusX);
        Assert.Equal(0, zeroEllipse.RadiusY);
        Assert.Equal(zeroEllipse.Center, negEllipse.Center);
    }

    [Fact]
    public void CreatePieGeometry_OverHundredPercent_BoundaryTreatedAsFullDisk()
    {
        Geometry over = PieChartRenderer.CreatePieGeometry(150, BorderThickness);
        Geometry at = PieChartRenderer.CreatePieGeometry(100, BorderThickness);

        var overEllipse = Assert.IsType<EllipseGeometry>(over);
        var atEllipse = Assert.IsType<EllipseGeometry>(at);
        double expectedRadius = Constants.Display.RamMeterRadius - BorderThickness / 2.0;
        Assert.Equal(expectedRadius, overEllipse.RadiusX, precision: 6);
        Assert.Equal(expectedRadius, overEllipse.RadiusY, precision: 6);
        Assert.Equal(atEllipse.Center, overEllipse.Center);
    }

    [Fact]
    public void CreatePieGeometry_HugeBorderThickness_ClampsRadiusToZero()
    {
        // boundary thickness equal to the meter's full radius shrinks the wedge radius to 0,
        // but the code path must still produce a non-throwing geometry.
        Geometry g = PieChartRenderer.CreatePieGeometry(50, borderThickness: Constants.Display.RamMeterRadius * 4);

        Assert.NotNull(g);
    }

    [Fact]
    public void UpdatePieWithCache_BelowThreshold_ReusesCachedGeometry()
    {
        // WPF FrameworkElement (Path, Shape, etc.) requires the calling thread to have
        // ApartmentState.STA on construction; xUnit runs on MTA by default. The body of
        // any test that does `new WpfPath()` therefore jumps to a dedicated STA thread via
        // the RunOnStaThread helper at the bottom of this class. Assertions happen back
        // on the MTA test thread over the captured Geometry references, which are safe to
        // touch cross-thread (WPF Geometry is free-threaded for frozen instances).
        Geometry? first = null;
        Geometry? second = null;

        RunOnStaThread(() =>
        {
            var pie = new WpfPath();
            Geometry? cached = null;
            double pct = -1;
            PieChartRenderer.UpdatePieWithCache(pie, 30, ref cached, ref pct, BorderThickness);
            first = pie.Data;
            PieChartRenderer.UpdatePieWithCache(pie, 30.05, ref cached, ref pct, BorderThickness);
            second = pie.Data;
        });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public void UpdatePieWithCache_AboveThreshold_RecreatesGeometry()
    {
        Geometry? first = null;
        Geometry? second = null;

        RunOnStaThread(() =>
        {
            var pie = new WpfPath();
            Geometry? cached = null;
            double pct = -1;
            PieChartRenderer.UpdatePieWithCache(pie, 30, ref cached, ref pct, BorderThickness);
            first = pie.Data;
            // Above threshold (30 + 5 = 35, delta 5 > 0.1)
            PieChartRenderer.UpdatePieWithCache(pie, 35, ref cached, ref pct, BorderThickness);
            second = pie.Data;
        });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void UpdatePieWithCache_NullPath_Throws()
    {
        Geometry? cachedGeometry = null;
        double cachedPercentage = -1;

        Assert.Throws<ArgumentNullException>(() =>
            PieChartRenderer.UpdatePieWithCache(null!, 50, ref cachedGeometry, ref cachedPercentage, BorderThickness));
    }

    /// <summary>
    /// Spawns a dedicated Thread with <see cref="ApartmentState.STA"/> and runs the
    /// supplied work synchronously on it. Required for tests that instantiate a WPF
    /// <see cref="FrameworkElement"/> (Path, Shape, Window, ...) because those classes'
    /// constructors throw <c>InvalidOperationException</c> on MTA threads (which is the
    /// default xUnit test thread model).
    ///
    /// Any exception thrown inside <paramref name="work"/> is re-thrown on the calling
    /// thread wrapped in an <see cref="InvalidOperationException"/> so the original
    /// stack trace is preserved.
    ///
    /// Note: this is a tiny shim that predates the <c>Xunit.StaFact</c> NuGet. We
    /// keep it inline so the test project does not require a third-party STA package;
    /// add <c>Xunit.StaFact</c> if more tests need STA in the future.
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

        // Rethrow the original exception on the calling thread, preserving its stack
        // trace instead of burying it under an InvalidOperationException wrapper.
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
