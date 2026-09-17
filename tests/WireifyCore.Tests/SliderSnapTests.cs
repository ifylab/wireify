// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class SliderSnapTests
{
    [Theory]
    [InlineData(SliderAccuracy.Integer, 0, 1)]
    [InlineData(SliderAccuracy.Integer, 3, 1)] // stale decimals on an integer slider never leak into the step
    [InlineData(SliderAccuracy.Even, 0, 2)]
    [InlineData(SliderAccuracy.Odd, 2, 2)]
    [InlineData(SliderAccuracy.Float, 0, 1)]
    [InlineData(SliderAccuracy.Float, 2, 0.01)]
    public void Step_follows_the_accuracy_not_just_the_decimals(SliderAccuracy accuracy, int decimals, double expected)
        => Assert.Equal(expected, SliderSnap.StepFor(accuracy, decimals), 12);

    [Theory]
    [InlineData(6.238, 6)]
    [InlineData(6.9, 7)]
    [InlineData(6.5, 7)] // midpoints round away from zero, matching how a drag feels
    [InlineData(7.0, 7)]
    public void Integer_sliders_snap_to_whole_numbers(double pushed, double expected)
        => Assert.Equal(expected, SliderSnap.Snap(0, 10, SliderAccuracy.Integer, 0, pushed));

    [Theory]
    [InlineData(5.2, 6)]
    [InlineData(4.9, 4)]
    [InlineData(8, 8)]
    public void Even_sliders_snap_to_even_numbers(double pushed, double expected)
        => Assert.Equal(expected, SliderSnap.Snap(0, 10, SliderAccuracy.Even, 0, pushed));

    [Theory]
    [InlineData(5.4, 5)]
    [InlineData(6.2, 7)]
    [InlineData(3, 3)]
    public void Odd_sliders_snap_to_odd_numbers(double pushed, double expected)
        => Assert.Equal(expected, SliderSnap.Snap(1, 9, SliderAccuracy.Odd, 0, pushed));

    [Fact]
    public void Float_sliders_round_to_their_decimal_places()
        => Assert.Equal(2.35, SliderSnap.Snap(0, 10, SliderAccuracy.Float, 2, 2.34999), 12);

    [Fact]
    public void Values_clamp_into_the_domain_before_snapping()
    {
        Assert.Equal(10, SliderSnap.Snap(0, 10, SliderAccuracy.Integer, 0, 42));
        Assert.Equal(0, SliderSnap.Snap(0, 10, SliderAccuracy.Integer, 0, -3));
    }

    [Fact]
    public void Snapping_never_leaves_the_domain()
    {
        // Nearest even to 9 on [0, 9] is 10 — out of domain, so the last in-domain even wins.
        Assert.Equal(8, SliderSnap.Snap(0, 9, SliderAccuracy.Even, 0, 9));
        // Nearest odd to 0.4 on [0.5, 9] rounds to 1 — fine; but 0.5 itself must not snap below min.
        Assert.Equal(1, SliderSnap.Snap(0.5, 9, SliderAccuracy.Odd, 0, 0.5));
    }

    [Fact]
    public void A_domain_with_no_grid_point_falls_back_to_the_clamped_value()
        => Assert.Equal(3.4, SliderSnap.Snap(3.2, 3.4, SliderAccuracy.Even, 0, 5), 12);

    [Fact]
    public void Reversed_bounds_are_tolerated()
        => Assert.Equal(7, SliderSnap.Snap(10, 0, SliderAccuracy.Integer, 0, 6.9));
}
