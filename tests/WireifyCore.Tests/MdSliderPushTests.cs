// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>Pure MD-slider push contract: comma-separated axis strings in (the shape that keeps
/// the app surface's number|string|boolean value contract untouched), per-axis interval clamping
/// that tolerates canvas intervals authored in either direction.</summary>
public class MdSliderPushTests
{
    [Theory]
    [InlineData("0.25,0.8", new[] { 0.25, 0.8 })]
    [InlineData(" 0.25 , 0.8 ", new[] { 0.25, 0.8 })] // spaces tolerated
    [InlineData("1,2,3", new[] { 1.0, 2.0, 3.0 })] // 3-axis form parses; the bridge checks count
    [InlineData("-1.5,2e2", new[] { -1.5, 200.0 })]
    [InlineData("7", new[] { 7.0 })] // count validation is the bridge's, not the parser's
    public void Parses_comma_separated_axis_values(string text, double[] expected)
    {
        Assert.True(MdSliderPush.TryParse(text, out var values));
        Assert.Equal(expected, values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0.2,")] // trailing empty part
    [InlineData("a,b")]
    [InlineData("0.2;0.8")] // wrong separator
    [InlineData("NaN,0.5")] // NaN/Infinity never reach the canvas
    [InlineData("Infinity,0.5")]
    public void Refuses_unparseable_text(string? text)
        => Assert.False(MdSliderPush.TryParse(text, out _));

    [Theory]
    [InlineData(0.5, 0, 1, 0.5)] // inside
    [InlineData(-2, 0, 1, 0)] // below
    [InlineData(9, 0, 1, 1)] // above
    [InlineData(0.5, 1, 0, 0.5)] // decreasing interval normalizes
    [InlineData(-2, 1, 0, 0)]
    public void Clamps_into_the_interval_whatever_its_direction(double value, double a, double b, double expected)
        => Assert.Equal(expected, MdSliderPush.Clamp(value, a, b));

    // GH_MultiDimensionalSlider.Value stores fractions of the interval; X/Y/Z evaluate
    // back through it (X = T0 + Value.X * span). The round-4 defect wrote domain values
    // straight in: x=25 on a 0–100 axis landed the canvas point at 2500.
    [Theory]
    [InlineData(25, 0, 100, 0.25)]
    [InlineData(0, 0, 100, 0)]
    [InlineData(100, 0, 100, 1)]
    [InlineData(-5, -10, 10, 0.25)]
    [InlineData(25, 100, 0, 0.75)] // decreasing interval: fraction runs from T0
    [InlineData(7, 7, 7, 0)] // zero-span interval cannot divide
    public void Normalizes_domain_values_onto_the_interval(double domain, double t0, double t1, double expected)
        => Assert.Equal(expected, MdSliderPush.Normalize(domain, t0, t1), 12);
}
