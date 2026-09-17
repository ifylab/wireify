// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyCore.Bridge
{
    /// <summary>An axis-aligned canvas rectangle (GH canvas coordinates: +Y is down).</summary>
    public sealed record PlacementRect(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;

        public bool Intersects(PlacementRect other, float margin)
            => X < other.Right + margin && other.X < Right + margin
            && Y < other.Bottom + margin && other.Y < Bottom + margin;
    }

    /// <summary>
    /// Deterministic overlap-free placement for agent-created objects. Every control anchored to
    /// the same component used to land on the identical pivot — four inputs, four controls, one
    /// spot (round-7 item 3). The canonical Grasshopper convention (what Extract Parameter does,
    /// and what engineers do by hand) is a vertical column left of the component; this helper
    /// supplies the missing half: slide a desired rectangle DOWN past whatever already occupies
    /// its spot, so repeated creates stack into a column instead of a pile. Pure geometry, no GH
    /// types — the bridge feeds it the live bounds.
    /// </summary>
    public static class CanvasPlacement
    {
        /// <summary>The gap a stacked (bare <c>nearId</c>) placement keeps from what it slides
        /// past. Row-anchored controls settle with NO margin: the input pitch equals a slider's
        /// height, so any gap pushes the next input's control off its row and the error grows
        /// down the column (round-8 F2 measured +12 px per row).</summary>
        public const float StackMargin = 12f;

        /// <summary>The Extract Parameter slot: a rectangle of the given size whose RIGHT edge sits
        /// <paramref name="gap"/> left of <paramref name="anchorLeft"/> and whose vertical centre
        /// is <paramref name="rowY"/> (the input grip's row). Round 7 measured the previous
        /// placement landing inside the component and covering its labels — the rect, not the
        /// pivot, is what must clear the component.</summary>
        public static PlacementRect ColumnSlot(float anchorLeft, float rowY, float width, float height, float gap = 30f)
            => new(anchorLeft - gap - width, rowY - height / 2f, width, height);

        /// <summary>The pivot that puts a rectangle's top-left at (<paramref name="left"/>,
        /// <paramref name="top"/>), given where the object's pivot sits inside its own bounds
        /// (<paramref name="offsetX"/>/<paramref name="offsetY"/> = bounds top-left minus pivot,
        /// as measured by laying the attributes out once). Zero offsets = a top-left pivot (the
        /// special-object attribute classes); negative half-sizes = a centred pivot.</summary>
        public static (float X, float Y) PivotFor(float left, float top, float offsetX, float offsetY)
            => (left - offsetX, top - offsetY);

        /// <summary>The gap within which an object to the RIGHT of a control counts as its
        /// anchor: the column slot's own 30 px plus room for a hand nudge.</summary>
        public const float AnchorGap = 60f;

        /// <summary>True when some <paramref name="neighbours"/> rect sits immediately right of
        /// <paramref name="before"/> on its row — its left edge within <paramref name="maxGap"/>
        /// of the control's right edge, and the two overlapping vertically. The rule a rename
        /// keeps the right edge by: round 11 (S11.22) renamed a control that was placed against
        /// its component but not yet wired to it, and a wiring-only anchor test let the capsule
        /// grow rightward over the very input it was placed for.</summary>
        public static bool AnchoredToRight(PlacementRect before, IEnumerable<PlacementRect> neighbours, float maxGap = AnchorGap)
        {
            if (before is null) throw new ArgumentNullException(nameof(before));
            if (neighbours is null) throw new ArgumentNullException(nameof(neighbours));
            if (before.Width <= 0) return false;
            foreach (var n in neighbours)
            {
                if (n is null || n.Width <= 0) continue;
                var toTheRight = n.X >= before.Right - 1f && n.X <= before.Right + maxGap;
                var sameRow = n.Y < before.Bottom && n.Bottom > before.Y;
                if (toTheRight && sameRow) return true;
            }
            return false;
        }

        /// <summary>Settle <paramref name="desired"/> downward until it clears every
        /// <paramref name="occupied"/> rect by <paramref name="margin"/>. Jumps below the
        /// lowest intersecting rect per step (fast convergence, no creeping); the step cap is a
        /// runaway guard for pathological canvases, not a tuning knob. Returns the settled
        /// top-left.</summary>
        public static (float X, float Y) Settle(
            PlacementRect desired, IReadOnlyList<PlacementRect> occupied, float margin = StackMargin, int maxSteps = 64)
        {
            if (desired is null) throw new ArgumentNullException(nameof(desired));
            if (occupied is null) throw new ArgumentNullException(nameof(occupied));

            var current = desired;
            for (var step = 0; step < maxSteps; step++)
            {
                float? pushBelow = null;
                foreach (var rect in occupied)
                    if (current.Intersects(rect, margin))
                        pushBelow = Math.Max(pushBelow ?? float.MinValue, rect.Bottom);
                if (pushBelow is not { } bottom) return (current.X, current.Y);
                current = current with { Y = bottom + margin };
            }
            return (current.X, current.Y);
        }
    }
}
