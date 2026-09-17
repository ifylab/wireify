// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Pure choice of WHICH output param a component-addressed app view shows. Every script
    /// component's first output is the stdout <c>out</c> param, so "first output" as a default
    /// pinned every view card to an empty placeholder and hid the real data (the PC-round bug).
    /// A manifest entry may name the output explicitly (<c>param</c>); without one, the first
    /// non-stdout output wins, falling back to the first output only when nothing else exists.
    /// </summary>
    public static class AppViewSelection
    {
        public const string StdOutKey = "out";

        /// <summary>Index into <paramref name="outputKeys"/> of the output to show, or -1 with
        /// an error naming what was asked for. Keys compare case-insensitively; with duplicate
        /// keys the first match wins (rename the params to disambiguate — or address the param
        /// by its own guid in the manifest instead).</summary>
        public static (int Index, string? Error) PickOutput(IReadOnlyList<string> outputKeys, string? wanted)
        {
            if (!string.IsNullOrEmpty(wanted))
            {
                for (var i = 0; i < outputKeys.Count; i++)
                    if (string.Equals(outputKeys[i], wanted, StringComparison.OrdinalIgnoreCase))
                        return (i, null);
                return (-1, $"no output '{wanted}' on this component");
            }

            if (outputKeys.Count == 0) return (-1, "component has no outputs");
            for (var i = 0; i < outputKeys.Count; i++)
                if (!string.Equals(outputKeys[i], StdOutKey, StringComparison.OrdinalIgnoreCase))
                    return (i, null);
            return (0, null); // only the stdout param exists — show it rather than nothing
        }
    }
}
