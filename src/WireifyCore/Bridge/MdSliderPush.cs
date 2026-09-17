// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Pure MD-slider push parsing/clamping. A push arrives as comma-separated axis values
    /// ("0.25,0.8" — the string shape keeps the app surface's number|string|boolean value
    /// contract untouched); the bridge validates the count against the slider's own mode and
    /// clamps each axis into its interval, which may be authored decreasing on the canvas.
    /// </summary>
    public static class MdSliderPush
    {
        public static bool TryParse(string? text, out IReadOnlyList<double> values)
        {
            values = Array.Empty<double>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Split(',');
            var parsed = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i])
                    || double.IsNaN(parsed[i]) || double.IsInfinity(parsed[i]))
                    return false;
            }
            values = parsed;
            return true;
        }

        public static double Clamp(double value, double boundA, double boundB)
        {
            var min = Math.Min(boundA, boundB);
            var max = Math.Max(boundA, boundB);
            return Math.Min(Math.Max(value, min), max);
        }

        /// <summary>Domain value -> the 0..1 fraction GH_MultiDimensionalSlider.Value actually
        /// stores (its X/Y/Z read back through the interval: X = T0 + Value.X * span). Writing a
        /// domain value into Value directly lands span-times off — the round-4 defect where
        /// x=25 on a 0–100 axis parked the canvas point at 2500. Interval direction (T0 &gt; T1)
        /// is honored; a zero-span interval normalizes to 0.</summary>
        public static double Normalize(double domainValue, double t0, double t1)
        {
            var span = t1 - t0;
            if (span == 0) return 0;
            return (domainValue - t0) / span;
        }
    }
}
