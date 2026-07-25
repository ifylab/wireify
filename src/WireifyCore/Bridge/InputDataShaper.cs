// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace WireifyCore.Bridge
{
    /// <summary>One item on a branch, already read out of the Grasshopper goo into strings.
    /// <c>Goo</c> is the wrapper class name (e.g. <c>GH_Line</c>), empty for non-goo items;
    /// <c>Value</c> is the full string form — the shaper caps it when sampling.</summary>
    public sealed record ShapedItem(string TypeName, string Clr, string Goo, string Value);

    /// <summary>One data-tree branch: its path plus the items on it.</summary>
    public sealed record ShapedBranch(string Path, IReadOnlyList<ShapedItem> Items);

    /// <summary>
    /// Shapes a wired input's branches into the <see cref="InputData"/> contract: tree stats, a type
    /// histogram over every item, and capped samples (first N per branch, M total). Pure — the bridge
    /// reads VolatileData into branches and this does the shaping, so the edge feature (reading live
    /// wired-input data) is unit-tested without a Rhino install.
    /// </summary>
    public static class InputDataShaper
    {
        /// <summary>Sample values are capped at this many characters; the sample still reports the
        /// full length, so truncated upstream data (the 32767 panel clip) is detectable at a glance.</summary>
        public const int MaxSampleValueChars = 2048;

        /// <summary>Runtime reports carry at most this many values per output, each value capped,
        /// plus the true total — a heavy output must not flood every set_source/run response. The
        /// per-value budget is generous enough for a debug/probe JSON dump in a single value (the
        /// schema-probe workflow); the value-count cap is what holds the line on bulk data.
        /// <see cref="MaxReportPerBranch"/> keeps multi-branch outputs represented instead of one
        /// branch consuming the whole sample budget.</summary>
        public const int MaxReportValues = 25;
        public const int MaxReportValueChars = 8192;
        public const int MaxReportPerBranch = 10;

        public static InputData Shape(
            string param,
            string access,
            IReadOnlyList<ShapedBranch> branches,
            int maxPerBranch,
            int maxTotal,
            int maxValueChars = MaxSampleValueChars)
        {
            if (branches is null) throw new ArgumentNullException(nameof(branches));
            if (maxPerBranch < 0) throw new ArgumentOutOfRangeException(nameof(maxPerBranch));
            if (maxTotal < 0) throw new ArgumentOutOfRangeException(nameof(maxTotal));

            var pathCount = branches.Count;
            var dataCount = branches.Sum(b => b.Items.Count);
            var tree = new TreeInfo(pathCount, dataCount, pathCount <= 1);

            var types = branches
                .SelectMany(b => b.Items)
                .GroupBy(it => it.TypeName)
                .Select(g => new TypeCount(g.Key, g.First().Clr, g.Count(), g.First().Goo))
                .ToList();

            var samples = new List<DataSample>();
            var clipped = 0;
            foreach (var branch in branches)
            {
                if (samples.Count >= maxTotal) break;
                var taken = 0;
                foreach (var item in branch.Items)
                {
                    if (taken >= maxPerBranch || samples.Count >= maxTotal) break;
                    var (value, fullLength) = Cap(item.Value, maxValueChars);
                    if (fullLength == WireifyContract.WireifyIds.PanelClipTextLength) clipped++;
                    samples.Add(new DataSample(branch.Path, value, item.TypeName, fullLength));
                    taken++;
                }
            }

            var warnings = clipped == 0
                ? null
                : new List<string> { WireifyContract.WireifyIds.ClipTextWarning(param, clipped) };
            return new InputData(param, access, tree, types, samples, warnings);
        }

        /// <summary>Cap a value string for transport, returning the (possibly shortened) text plus
        /// its true full length. The transported text is sanitized: canvas data can carry lone
        /// surrogates or stray control characters (and the cap itself can cut a surrogate pair in
        /// half) — none of that may ever be able to fail the response's JSON serialization, which
        /// happens outside the tool error handling and would mask the whole result. The reported
        /// FullLength is always the ORIGINAL length (clip detection depends on it exactly).</summary>
        public static (string Value, int FullLength) Cap(string value, int maxChars)
        {
            if (value is null) return ("", 0);
            if (maxChars < 0) maxChars = 0;
            var text = value.Length <= maxChars ? value : value.Substring(0, maxChars) + "…";
            return (Sanitize(text), value.Length);
        }

        static string Sanitize(string text)
        {
            var clean = true;
            for (var i = 0; i < text.Length && clean; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                    else clean = false;
                }
                else if (char.IsLowSurrogate(c)) clean = false;
                else if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') clean = false;
            }
            if (clean) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                }
                else if (char.IsSurrogate(c) || (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                {
                    sb.Append('�');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
