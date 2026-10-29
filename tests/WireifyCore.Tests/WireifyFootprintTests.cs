// SPDX-License-Identifier: Apache-2.0
using WireifyContract;

namespace WireifyCore.Tests;

public class WireifyFootprintTests
{
    [Fact]
    public void A_plate_wider_than_the_body_widens_the_footprint_only_sideways()
    {
        var body = (X: 100f, Y: 50f, Width: 120f, Height: 140f); // Bounds already reach the plate bottom
        var plate = (X: 100f, Y: 96f, Width: 330f, Height: 94f);

        Assert.Equal((100f, 50f, 330f, 140f), WireifyFootprint.Union(body, plate));
    }

    [Fact]
    public void An_empty_side_contributes_nothing()
    {
        var body = (X: 10f, Y: 20f, Width: 30f, Height: 40f);

        Assert.Equal(body, WireifyFootprint.Union(body, (0f, 0f, 0f, 0f)));
        Assert.Equal(body, WireifyFootprint.Union((0f, 0f, 0f, 0f), body));
    }
}
