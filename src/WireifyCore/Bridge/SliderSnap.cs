// SPDX-License-Identifier: Apache-2.0
using System;

namespace WireifyCore.Bridge
{
    /// <summary>A GH number slider's rounding mode, mirrored Rhino-type-free so the snapping
    /// logic stays pure and unit-testable (the bridge maps <c>GH_SliderAccuracy</c> onto it).</summary>
    public enum SliderAccuracy
    {
        Float,
        Integer,
        Even,
        Odd,
    }

    /// <summary>
    /// Pure slider-value snapping. A push from the companion app carries whatever number the
    /// browser control produced; the canvas slider's own accuracy (float/integer/even/odd) and
    /// decimal places decide what it may actually hold. Snapping here — clamp into the domain,
    /// round onto the accuracy's grid, and step back inside if the rounding left the domain —
    /// keeps the applied value deterministic instead of leaning on whatever the GH setter does
    /// with an off-grid number.
    /// </summary>
    public static class SliderSnap
    {
        const int MaxDecimals = 12;

        /// <summary>The browser-facing step for a slider of this accuracy — what an HTML range
        /// control should use so drags land on values the canvas slider can hold.</summary>
        public static double StepFor(SliderAccuracy accuracy, int decimals) => accuracy switch
        {
            SliderAccuracy.Integer => 1,
            SliderAccuracy.Even or SliderAccuracy.Odd => 2,
            _ => Math.Pow(10, -Clamp(decimals, 0, MaxDecimals)),
        };

        /// <summary>Clamp <paramref name="value"/> into [min, max], then snap it onto the
        /// accuracy's grid, preferring the nearest grid point that is still inside the domain.
        /// A domain that contains no grid point at all (never true for a GH-authored slider)
        /// falls back to the plain clamped value.</summary>
        public static double Snap(double min, double max, SliderAccuracy accuracy, int decimals, double value)
        {
            if (max < min) (min, max) = (max, min);
            var clamped = Math.Min(Math.Max(value, min), max);

            var snapped = accuracy switch
            {
                SliderAccuracy.Integer => Math.Round(clamped, MidpointRounding.AwayFromZero),
                SliderAccuracy.Even => Math.Round(clamped / 2, MidpointRounding.AwayFromZero) * 2,
                SliderAccuracy.Odd => Math.Round((clamped - 1) / 2, MidpointRounding.AwayFromZero) * 2 + 1,
                _ => Math.Round(clamped, Clamp(decimals, 0, MaxDecimals)),
            };

            if (snapped < min) snapped = FirstOnGridAtOrAbove(min, accuracy, decimals);
            else if (snapped > max) snapped = LastOnGridAtOrBelow(max, accuracy, decimals);

            return snapped >= min && snapped <= max ? snapped : clamped;
        }

        static double FirstOnGridAtOrAbove(double bound, SliderAccuracy accuracy, int decimals)
        {
            switch (accuracy)
            {
                case SliderAccuracy.Integer: return Math.Ceiling(bound);
                case SliderAccuracy.Even: return Math.Ceiling(bound / 2) * 2;
                case SliderAccuracy.Odd: return Math.Ceiling((bound - 1) / 2) * 2 + 1;
                default:
                    var scale = Math.Pow(10, Clamp(decimals, 0, MaxDecimals));
                    return Math.Ceiling(bound * scale) / scale;
            }
        }

        static double LastOnGridAtOrBelow(double bound, SliderAccuracy accuracy, int decimals)
        {
            switch (accuracy)
            {
                case SliderAccuracy.Integer: return Math.Floor(bound);
                case SliderAccuracy.Even: return Math.Floor(bound / 2) * 2;
                case SliderAccuracy.Odd: return Math.Floor((bound - 1) / 2) * 2 + 1;
                default:
                    var scale = Math.Pow(10, Clamp(decimals, 0, MaxDecimals));
                    return Math.Floor(bound * scale) / scale;
            }
        }

        static int Clamp(int value, int lo, int hi) => Math.Max(lo, Math.Min(hi, value));
    }
}
