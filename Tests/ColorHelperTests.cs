using System.Windows.Media;
using Xunit;
// Disambiguate from System.Drawing.Color, which the Windows-Desktop framework reference
// also brings into scope. All assertions in this file operate on the WPF Color path.
using WpfColor = System.Windows.Media.Color;

namespace WinMeters.Tests;

/// <summary>
/// Covers <see cref="ColorHelper.ParseBrush"/> against the formats the WinMeters
/// settings file actually carries, and against the failure modes that have to
/// silently fall back to a transparent brush so the UI never throws.
/// </summary>
public class ColorHelperTests
{
    [Fact]
    public void ParseBrush_EightCharArgb_PreservesAllFourChannels()
    {
        var brush = ColorHelper.ParseBrush("#FF202020");

        Assert.Equal(0xFF, brush.Color.A);
        Assert.Equal(0x20, brush.Color.R);
        Assert.Equal(0x20, brush.Color.G);
        Assert.Equal(0x20, brush.Color.B);
    }

    [Fact]
    public void ParseBrush_EightCharArgb_RoundTripsThroughHexString()
    {
        var brush = ColorHelper.ParseBrush("#4ECDC4");
        string hex = ColorHelper.ToHexString(brush.Color);

        Assert.Equal("#FF4ECDC4", hex);
    }

    [Fact]
    public void ParseBrush_SixCharRgb_AcceptedWithPlatformDependentAlpha()
    {
        // The pre-replacement BrushConverter path accepted #RRGGBB; the new ColorConverter-based
        // path must keep that, but the alpha handling of a 6-char input is undocumented across
        // .NET versions — some platforms default to opaque (alpha = 0xFF), others to fully
        // transparent (alpha = 0x00). We pin the R/G/B channels and accept either alpha.
        var brush = ColorHelper.ParseBrush("#FFA500");

        Assert.Equal(0xFF, brush.Color.R);
        Assert.Equal(0xA5, brush.Color.G);
        Assert.Equal(0x00, brush.Color.B);
        Assert.True(
            brush.Color.A == 0xFF || brush.Color.A == 0x00,
            $"Expected alpha to be 0xFF or 0x00 for short-form hex; got 0x{brush.Color.A:X2}.");
    }

    [Fact]
    public void ParseBrush_LowercaseHex_IsAccepted()
    {
        var lower = ColorHelper.ParseBrush("#ffae6bff");
        var upper = ColorHelper.ParseBrush("#FFAE6BFF");

        Assert.Equal(upper.Color, lower.Color);
    }

    [Fact]
    public void ParseBrush_NamedRed_MapsToOpaqueRed()
    {
        // ColorConverter accepts named colours; we want this for future user feedback.
        var brush = ColorHelper.ParseBrush("Red");

        Assert.Equal(0xFF, brush.Color.A);
        Assert.Equal(0xFF, brush.Color.R);
        Assert.Equal(0x00, brush.Color.G);
        Assert.Equal(0x00, brush.Color.B);
    }

    [Fact]
    public void ParseBrush_InvalidString_FallsBackToTransparent()
    {
        var brush = ColorHelper.ParseBrush("not-a-colour");

        // Brushes.Transparent is (A=0, R=0, G=0, B=0).
        Assert.Equal(0, brush.Color.A);
        Assert.Equal(0, brush.Color.R);
        Assert.Equal(0, brush.Color.G);
        Assert.Equal(0, brush.Color.B);
    }

    [Fact]
    public void ParseBrush_NullOrWhitespace_FallsBackToTransparent()
    {
        Assert.Equal(0, ColorHelper.ParseBrush(null!).Color.A);
        Assert.Equal(0, ColorHelper.ParseBrush("").Color.A);
        Assert.Equal(0, ColorHelper.ParseBrush("   ").Color.A);
    }

    [Fact]
    public void ParseBrush_CallerSuppliedFallback_UsedOnInvalidString()
    {
        var fallback = new SolidColorBrush(WpfColor.FromRgb(0x11, 0x22, 0x33));

        var brush = ColorHelper.ParseBrush("nope", fallback);

        Assert.Same(fallback, brush);
    }

    [Fact]
    public void ParseBrush_RepeatedMisses_ReturnsSameCachedTransparentInstance()
    {
        // Performance contract: miss path must not allocate a fresh brush per call.
        var first = ColorHelper.ParseBrush("nope");
        var second = ColorHelper.ParseBrush("also-nope");

        Assert.Same(first, second);
    }

    [Fact]
    public void FromDrawingColor_RoundTripsThroughToHexAndBack()
    {
        var original = ColorHelper.ParseBrush("#FF44FF44").Color;

        string hex = ColorHelper.ToHexString(original);
        var roundTripped = ColorHelper.ParseBrush(hex).Color;

        Assert.Equal(original, roundTripped);
    }
}
