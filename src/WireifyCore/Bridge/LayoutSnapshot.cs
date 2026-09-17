// SPDX-License-Identifier: Apache-2.0
namespace WireifyCore.Bridge
{
    /// <summary>One canvas object's name and layout at an instant: what a rename's undo must
    /// put back as a whole. Grasshopper's own layout undo restored the pivot but not the width,
    /// and its slider layout is max(current width, fitted width) — so after ctrl-Z the
    /// post-rename width survived under the pre-rename name and pivot, and the capsule
    /// overshot rightward by the whole width delta, over the input it fed (round-10 S10.8).
    /// Pure data; the Grasshopper-side action applies it.</summary>
    public sealed record LayoutSnapshot(
        string NickName, float X, float Y, float Width, float Height, float PivotX, float PivotY)
    {
        public float Right => X + Width;
    }
}
