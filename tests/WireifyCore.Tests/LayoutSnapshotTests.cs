// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

/// <summary>The value a rename's undo restores as a whole (round-10 S10.8): name, bounds and
/// pivot together, so the right edge comes back with them.</summary>
public class LayoutSnapshotTests
{
    [Fact]
    public void A_snapshot_carries_its_right_edge_and_compares_by_value()
    {
        var before = new LayoutSnapshot("module_count", 306, 508, 202, 20, 306, 508);
        var after = new LayoutSnapshot("module_count_along_the_rope_axis", 148, 508, 360, 20, 148, 508);

        // The rename grew the capsule leftward: same right edge on both sides is the anchor
        // invariant the tool path keeps and the undo must keep too.
        Assert.Equal(508, before.Right);
        Assert.Equal(508, after.Right);
        Assert.Equal(before, new LayoutSnapshot("module_count", 306, 508, 202, 20, 306, 508));
        Assert.NotEqual(before, after);
    }
}
