using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
// Disambiguates the WPF Path type from System.IO.Path. Required because this file is
// pulled into the Tests project via <Compile Include>, where Microsoft.NET.Sdk's implicit
// usings add `using System.IO;` — without an alias the bare `Path` reference becomes
// ambiguous (CS0104).
using WpfPath = System.Windows.Shapes.Path;

namespace WinMeters.Utils;

/// <summary>
/// Pure-math helpers for the RAM / VRAM / SRAM pie meters. Stateless and side-effect free,
/// so the geometry can be unit-tested without bringing up a WPF dispatcher thread.
/// </summary>
/// <remarks>
/// <para>
/// The renderer is intentionally minimal:
/// <list type="bullet">
/// <item><see cref="CreatePieGeometry"/> builds a frozen <see cref="PathGeometry"/> cache.</item>
/// <item><see cref="UpdatePieWithCache"/> reuses the cached geometry when the percentage
/// delta is below the cache threshold, avoiding unnecessary per-tick allocations.</item>
/// </list>
/// </para>
/// <para>
/// Both methods take the border thickness as an explicit parameter so this class does not
/// have to know about <see cref="AppSettings"/>; the caller supplies the thickness from
/// its own settings model.
/// </para>
/// </remarks>
internal static class PieChartRenderer
{
    /// <summary>Percent-delta below which the cached geometry is reused.</summary>
    public const double CacheThresholdPercent = 0.1;

    /// <summary>
    /// Builds a frozen <see cref="PathGeometry"/> representing a pie wedge of the given
    /// percentage (0 ≤ pct ≤ 100). Returns an <see cref="EllipseGeometry"/> for the
    /// edge cases (0 -> empty, 100 -> full disk) so the rendered Path is meaningful
    /// regardless of input.
    /// </summary>
    public static Geometry CreatePieGeometry(double percentage, double borderThickness)
    {
        double radius = Constants.Display.RamMeterRadius - (borderThickness / 2.0);
        if (radius < 0) radius = 0;

        double centerX = Constants.Display.RamMeterRadius;
        double centerY = Constants.Display.RamMeterRadius;

        if (percentage >= 100)
        {
            return new EllipseGeometry(new WpfPoint(centerX, centerY), radius, radius);
        }
        if (percentage <= 0)
        {
            return new EllipseGeometry(new WpfPoint(centerX, centerY), 0, 0);
        }

        double angle = (percentage / 100.0) * 360.0;
        double rad = (angle - 90) * Math.PI / 180.0;
        double x = centerX + radius * Math.Cos(rad);
        double y = centerY + radius * Math.Sin(rad);
        bool isLargeArc = angle > 180.0;

        var pathFig = new PathFigure { StartPoint = new WpfPoint(centerX, centerY) };
        pathFig.Segments.Add(new LineSegment(new WpfPoint(centerX, centerY - radius), false));
        pathFig.Segments.Add(new ArcSegment(
            new WpfPoint(x, y),
            new WpfSize(radius, radius),
            rotationAngle: 0,
            isLargeArc: isLargeArc,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: false));
        pathFig.Segments.Add(new LineSegment(new WpfPoint(centerX, centerY), false));

        var geom = new PathGeometry();
        geom.Figures.Add(pathFig);
        geom.Freeze();
        return geom;
    }

    /// <summary>
    /// Caches the geometry assigned to <paramref name="pieElement"/>; only allocates a
    /// new <see cref="PathGeometry"/> when the percentage drifts by more than
    /// <see cref="CacheThresholdPercent"/>.
    /// </summary>
    /// <remarks>
    /// The cache references must outlive a single call (they live on the owning window);
    /// they are passed by reference to avoid allocating a per-call struct.
    /// </remarks>
    public static void UpdatePieWithCache(
        WpfPath pieElement,
        double percentage,
        ref Geometry? cachedGeometry,
        ref double cachedPercentage,
        double borderThickness)
    {
        if (pieElement is null) throw new ArgumentNullException(nameof(pieElement));

        if (Math.Abs(percentage - cachedPercentage) < CacheThresholdPercent && cachedGeometry != null)
        {
            pieElement.Data = cachedGeometry;
            return;
        }

        cachedPercentage = percentage;
        cachedGeometry = CreatePieGeometry(percentage, borderThickness);
        pieElement.Data = cachedGeometry;
    }
}
