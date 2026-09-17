// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

/// <summary>The geometric anchor a rename keeps its right edge by (round-11 S11.22: a control
/// placed against its component but not yet wired grew rightward over the input).</summary>
public class AnchorRuleTests
{
    static readonly PlacementRect Control = new(366, 508, 150, 20);

    [Fact]
    public void A_component_immediately_right_on_the_same_row_anchors_the_control()
    {
        var component = new PlacementRect(546, 353, 197, 284); // W1: 30 px right, spans the row
        Assert.True(CanvasPlacement.AnchoredToRight(Control, new[] { component }));
    }

    [Fact]
    public void Objects_far_right_above_below_or_to_the_left_do_not_anchor()
    {
        var farRight = new PlacementRect(700, 508, 100, 20);
        var above = new PlacementRect(546, 300, 100, 40);
        var below = new PlacementRect(546, 600, 100, 40);
        var left = new PlacementRect(100, 508, 100, 20);
        var overlapping = new PlacementRect(400, 508, 100, 20); // starts inside the control
        Assert.False(CanvasPlacement.AnchoredToRight(Control, new[] { farRight, above, below, left, overlapping }));
    }

    [Fact]
    public void The_gap_is_the_placement_gap_plus_a_nudge_and_touching_counts()
    {
        var touching = new PlacementRect(516, 500, 50, 40);
        var atTheLimit = new PlacementRect(516 + CanvasPlacement.AnchorGap, 500, 50, 40);
        var pastTheLimit = new PlacementRect(516 + CanvasPlacement.AnchorGap + 1, 500, 50, 40);
        Assert.True(CanvasPlacement.AnchoredToRight(Control, new[] { touching }));
        Assert.True(CanvasPlacement.AnchoredToRight(Control, new[] { atTheLimit }));
        Assert.False(CanvasPlacement.AnchoredToRight(Control, new[] { pastTheLimit }));
    }

    [Fact]
    public void An_unlaid_out_control_or_neighbour_never_anchors()
    {
        Assert.False(CanvasPlacement.AnchoredToRight(new PlacementRect(0, 0, 0, 0), new[] { new PlacementRect(10, 0, 50, 20) }));
        Assert.False(CanvasPlacement.AnchoredToRight(Control, new[] { new PlacementRect(520, 508, 0, 0) }));
    }
}
