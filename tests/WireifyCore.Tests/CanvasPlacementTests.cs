// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>The overlap fix's pure half (round-7 item 3): a desired spot slides DOWN past
/// occupied rects, so controls anchored to one component stack into a column instead of a
/// pile. The bridge feeds live bounds; these tests pin the geometry.</summary>
public class CanvasPlacementTests
{
    static PlacementRect R(float x, float y, float w = 100, float h = 20) => new(x, y, w, h);

    [Fact]
    public void A_clear_spot_is_taken_as_is()
    {
        var (x, y) = CanvasPlacement.Settle(R(0, 0), new[] { R(500, 500) });
        Assert.Equal((0f, 0f), (x, y));
    }

    [Fact]
    public void A_collision_slides_below_the_occupant_with_the_margin()
    {
        var (x, y) = CanvasPlacement.Settle(R(0, 0), new[] { R(0, 0) }, margin: 12f);
        Assert.Equal(0f, x);      // the column holds - only Y moves
        Assert.Equal(32f, y);     // occupant bottom (20) + margin
    }

    [Fact]
    public void Row_anchored_controls_sit_flush_on_their_input_rows()
    {
        // Four inputs at a 20 px pitch, four 20 px controls each centred on its grip row
        // (the bridge's nearInput path): settled without a margin the column matches the
        // pitch exactly; with the stacking margin every control after the first slides 12 px
        // further off its row - the drift round 8 measured as 0 / 12 / 24 / 36.
        var anchor = new PlacementRect(200, 100, 150, 100);
        var flush = new List<PlacementRect> { anchor };
        var stacked = new List<PlacementRect> { anchor };
        var flushDrift = new List<float>();
        var stackedDrift = new List<float>();
        for (var i = 0; i < 4; i++)
        {
            var gripY = 110f + 20f * i;
            var desired = CanvasPlacement.ColumnSlot(200f, gripY, 100f, 20f);

            var (x, y) = CanvasPlacement.Settle(desired, flush, margin: 0f);
            flushDrift.Add(y + 10f - gripY);
            flush.Add(new PlacementRect(x, y, 100f, 20f));

            var (xs, ys) = CanvasPlacement.Settle(desired, stacked, margin: CanvasPlacement.StackMargin);
            stackedDrift.Add(ys + 10f - gripY);
            stacked.Add(new PlacementRect(xs, ys, 100f, 20f));
        }

        Assert.All(flushDrift, d => Assert.Equal(0f, d));
        Assert.Equal(new[] { 0f, 12f, 24f, 36f }, stackedDrift);
        Assert.All(flush.Skip(1), r => Assert.True(r.Right <= anchor.X - 30f)); // clear of the body
    }

    [Fact]
    public void Repeated_placements_form_a_column()
    {
        // The bridge's actual sequence: each settled control becomes an occupant for the next.
        var occupied = new List<PlacementRect> { R(200, 100, 150, 40) }; // the anchor component
        var placed = new List<(float X, float Y)>();
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = CanvasPlacement.Settle(R(60, 100, 100, 20), occupied);
            placed.Add((x, y));
            occupied.Add(R(x, y, 100, 20));
        }

        Assert.Equal(4, placed.Select(p => p.Y).Distinct().Count()); // four rows, zero piles
        Assert.All(placed, p => Assert.Equal(60f, p.X));
        Assert.Equal(placed.OrderBy(p => p.Y), placed);              // stacked strictly downward
    }

    [Fact]
    public void The_slide_jumps_below_the_lowest_intersecting_rect()
    {
        // Two overlapping occupants: one hop clears both - no creeping through the stack.
        var (_, y) = CanvasPlacement.Settle(
            R(0, 0), new[] { R(0, 0, 100, 20), R(0, 10, 100, 60) }, margin: 10f);
        Assert.Equal(80f, y); // below the deeper rect's bottom (70) + margin
    }

    [Fact]
    public void Near_misses_outside_the_margin_do_not_move_the_spot()
    {
        var (x, y) = CanvasPlacement.Settle(R(0, 0, 100, 20), new[] { R(0, 33, 100, 20) }, margin: 12f);
        Assert.Equal((0f, 0f), (x, y)); // 13px of daylight beats the 12px margin
    }

    [Fact]
    public void ColumnSlot_puts_the_right_edge_a_gap_left_of_the_anchor_and_centres_on_the_row()
    {
        // Round-7 finding 4: controls landed INSIDE the component covering its labels. The
        // slot is a rectangle, and it is the rectangle's right edge that must clear the anchor.
        var slot = CanvasPlacement.ColumnSlot(anchorLeft: 400f, rowY: 120f, width: 180f, height: 20f, gap: 30f);
        Assert.Equal(370f, slot.Right);
        Assert.Equal(190f, slot.X);
        Assert.Equal(110f, slot.Y);            // centred on the grip row
        Assert.Equal(120f, slot.Y + slot.Height / 2f);
    }

    [Fact]
    public void PivotFor_maps_top_left_and_centred_pivot_conventions()
    {
        // A slider's pivot IS its top-left (offset 0,0); a component's pivot sits at its centre
        // (offset = minus half the size). Assuming the centre for a slider was the whole defect.
        Assert.Equal((190f, 110f), CanvasPlacement.PivotFor(190f, 110f, 0f, 0f));
        Assert.Equal((280f, 120f), CanvasPlacement.PivotFor(190f, 110f, -90f, -10f));
    }

    [Fact]
    public void Row_aligned_slots_at_the_input_pitch_never_slide()
    {
        // Four inputs 34 px apart, 20 px sliders: every slot clears its neighbour by 14 px, more
        // than the 12 px margin, so the column keeps the exact grip rows — no doubled stride.
        var anchor = R(400, 80, 150, 160);
        var occupied = new List<PlacementRect> { anchor };
        var rows = new[] { 100f, 134f, 168f, 202f };
        foreach (var rowY in rows)
        {
            var slot = CanvasPlacement.ColumnSlot(anchor.X, rowY, 180f, 20f);
            var (x, y) = CanvasPlacement.Settle(slot, occupied);
            Assert.Equal(slot.X, x);
            Assert.Equal(slot.Y, y);
            occupied.Add(new PlacementRect(x, y, 180f, 20f));
        }
        Assert.All(occupied.Skip(1), r => Assert.True(r.Right < anchor.X));
    }

    [Fact]
    public void The_step_cap_returns_a_position_rather_than_hanging()
    {
        // A pathological wall of occupants: the guard caps the walk and still answers.
        var wall = new List<PlacementRect>();
        for (var i = 0; i < 500; i++) wall.Add(R(0, i * 10, 100, 20));
        var (_, y) = CanvasPlacement.Settle(R(0, 0), wall, maxSteps: 8);
        Assert.True(y > 0);
    }
}
