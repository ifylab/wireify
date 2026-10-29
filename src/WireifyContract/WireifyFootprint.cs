// SPDX-License-Identifier: Apache-2.0
using System;

namespace WireifyContract
{
    /// <summary>
    /// Attributes that draw outside their Grasshopper <c>Bounds</c> implement this, so a bounds
    /// read reports what the user sees. The socket's plate can be wider than its body, but the
    /// body capsule is drawn from <c>Bounds</c>, so <c>Bounds</c> itself cannot widen.
    /// </summary>
    public interface IWireifyFootprint
    {
        /// <summary>The drawn footprint in canvas units; a zero width means not laid out yet.</summary>
        (float X, float Y, float Width, float Height) Footprint { get; }
    }

    public static class WireifyFootprint
    {
        /// <summary>The smallest rectangle holding both; an empty side (zero width or height)
        /// contributes nothing.</summary>
        public static (float X, float Y, float Width, float Height) Union(
            (float X, float Y, float Width, float Height) a,
            (float X, float Y, float Width, float Height) b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            if (b.Width <= 0 || b.Height <= 0) return a;
            var left = Math.Min(a.X, b.X);
            var top = Math.Min(a.Y, b.Y);
            var right = Math.Max(a.X + a.Width, b.X + b.Width);
            var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return (left, top, right - left, bottom - top);
        }
    }
}
