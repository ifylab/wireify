// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WireifyCore.Bridge
{
    /// <summary>What one data item reduces to for a comparison. Numeric kinds carry their
    /// coordinates in <see cref="Numbers"/> (a point is three, a line six, a plane twelve: origin
    /// and the three axes); <see cref="ComparableKind.Text"/> and the exact kinds carry
    /// <see cref="Text"/>; <see cref="ComparableKind.Unsupported"/> carries only its type name.</summary>
    public sealed record ComparableItem(ComparableKind Kind, string TypeName, IReadOnlyList<double>? Numbers = null, string? Text = null);

    public enum ComparableKind { Number, Integer, Boolean, Text, Point, Vector, Line, Plane, Interval, Colour, Unsupported }

    public sealed record ComparableBranch(string Path, IReadOnlyList<ComparableItem> Items);

    /// <summary>One output's data as read from the canvas. <c>Truncated</c> says the bridge
    /// stopped at its item cap, so the comparison covers a prefix of the data.</summary>
    public sealed record ComparableOutput(string Name, IReadOnlyList<ComparableBranch> Branches, bool Truncated = false);

    public sealed record OutputMismatch(string Output, string Path, int Index, string A, string B, string Reason);

    /// <summary><c>Verdict</c> is <c>equal</c>, <c>differs</c>, <c>unsupported</c> (the shape
    /// matched and every supported value matched, but some items are of a kind the comparison
    /// cannot judge), <c>missing-on-a</c> or <c>missing-on-b</c>.</summary>
    public sealed record OutputVerdict(
        string Output, string Verdict, int Paths, int ItemsA, int ItemsB, int Compared, int Mismatches, string? Note = null);

    /// <summary>The whole-component verdict. <c>AllEqual</c> is true only when every output on
    /// both sides matched within tolerance with nothing unsupported, nothing missing and nothing
    /// truncated — the only basis for claiming equivalence. <c>ManualCheck</c> names the outputs
    /// the comparison could not judge.</summary>
    public sealed record OutputComparison(
        bool AllEqual,
        double Tolerance,
        IReadOnlyList<OutputVerdict> Outputs,
        IReadOnlyList<OutputMismatch> Mismatches,
        bool MismatchesTruncated,
        IReadOnlyList<string> ManualCheck,
        string Summary);

    /// <summary>
    /// The mechanical half of the port flow's equivalence step: two components' outputs, matched
    /// by name, compared on tree shape (the path list, each branch's count) and on values —
    /// numbers and coordinates within an absolute tolerance, text and the exact kinds verbatim,
    /// anything else reported for a manual check rather than approximated. Pure: no Rhino types,
    /// so it is unit-tested without a host. The bridge reads the canvas into
    /// <see cref="ComparableOutput"/> lists and hands them here.
    /// </summary>
    public static class OutputComparer
    {
        const int MaxValueChars = 200;

        public static OutputComparison Compare(
            IReadOnlyList<ComparableOutput> a,
            IReadOnlyList<ComparableOutput> b,
            double tolerance,
            int maxMismatches = 10)
        {
            if (a is null) throw new ArgumentNullException(nameof(a));
            if (b is null) throw new ArgumentNullException(nameof(b));
            if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (maxMismatches < 0) throw new ArgumentOutOfRangeException(nameof(maxMismatches));

            var verdicts = new List<OutputVerdict>();
            var mismatches = new List<OutputMismatch>();
            var total = 0;
            var manual = new List<string>();
            var matchedB = new HashSet<int>();

            foreach (var outA in a)
            {
                var index = IndexOf(b, outA.Name, matchedB);
                if (index < 0)
                {
                    verdicts.Add(new OutputVerdict(outA.Name, "missing-on-b", outA.Branches.Count, Count(outA), 0, 0, 0));
                    continue;
                }
                matchedB.Add(index);
                var outB = b[index];
                var verdict = CompareOutput(outA, outB, tolerance, mismatches, maxMismatches, ref total);
                verdicts.Add(verdict);
                if (verdict.Verdict == "unsupported") manual.Add(outA.Name);
            }
            for (var i = 0; i < b.Count; i++)
            {
                if (matchedB.Contains(i)) continue;
                verdicts.Add(new OutputVerdict(b[i].Name, "missing-on-a", b[i].Branches.Count, 0, Count(b[i]), 0, 0));
            }

            var truncatedReads = verdicts.Where(v => v.Note is not null && v.Note.StartsWith("partial", StringComparison.Ordinal)).Count();
            var allEqual = verdicts.Count > 0 && verdicts.All(v => v.Verdict == "equal") && truncatedReads == 0;
            var summary = Summarize(verdicts, allEqual, manual, truncatedReads);
            return new OutputComparison(allEqual, tolerance, verdicts, mismatches, total > mismatches.Count, manual, summary);
        }

        static int IndexOf(IReadOnlyList<ComparableOutput> outputs, string name, HashSet<int> taken)
        {
            for (var i = 0; i < outputs.Count; i++)
                if (!taken.Contains(i) && string.Equals(outputs[i].Name, name, StringComparison.Ordinal)) return i;
            for (var i = 0; i < outputs.Count; i++)
                if (!taken.Contains(i) && string.Equals(outputs[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static int Count(ComparableOutput output) => output.Branches.Sum(br => br.Items.Count);

        static OutputVerdict CompareOutput(
            ComparableOutput a, ComparableOutput b, double tolerance,
            List<OutputMismatch> sink, int maxMismatches, ref int total)
        {
            var itemsA = Count(a);
            var itemsB = Count(b);
            var compared = 0;
            var count = 0;
            var unsupported = false;
            var notes = new List<string>();

            var pathsA = a.Branches.Select(br => br.Path).ToList();
            var pathsB = b.Branches.Select(br => br.Path).ToList();
            if (!pathsA.SequenceEqual(pathsB, StringComparer.Ordinal))
            {
                count++;
                Record(sink, maxMismatches, ref total,
                    new OutputMismatch(a.Name, "", -1, Paths(pathsA), Paths(pathsB), "paths differ"));
            }

            // Branches are compared by path, not by position, so one extra branch does not shift
            // every comparison after it.
            var byPathB = new Dictionary<string, ComparableBranch>(StringComparer.Ordinal);
            foreach (var br in b.Branches) byPathB[br.Path] = br;

            foreach (var brA in a.Branches)
            {
                if (!byPathB.TryGetValue(brA.Path, out var brB)) continue;
                if (brA.Items.Count != brB.Items.Count)
                {
                    count++;
                    Record(sink, maxMismatches, ref total,
                        new OutputMismatch(a.Name, brA.Path, -1, brA.Items.Count.ToString(CultureInfo.InvariantCulture),
                            brB.Items.Count.ToString(CultureInfo.InvariantCulture), "item count differs"));
                }
                var n = Math.Min(brA.Items.Count, brB.Items.Count);
                for (var i = 0; i < n; i++)
                {
                    var itemA = brA.Items[i];
                    var itemB = brB.Items[i];
                    var reason = CompareItem(itemA, itemB, tolerance, out var judged);
                    if (!judged) { unsupported = true; continue; }
                    compared++;
                    if (reason is null) continue;
                    count++;
                    Record(sink, maxMismatches, ref total,
                        new OutputMismatch(a.Name, brA.Path, i, Show(itemA), Show(itemB), reason));
                }
            }

            var kindsA = Kinds(a);
            var kindsB = Kinds(b);
            if (kindsA != kindsB && kindsA.Length > 0 && kindsB.Length > 0) notes.Add($"types differ: {kindsA} on A, {kindsB} on B");
            if (unsupported)
            {
                var names = a.Branches.SelectMany(br => br.Items).Where(it => it.Kind == ComparableKind.Unsupported)
                    .Select(it => it.TypeName).Distinct(StringComparer.Ordinal).ToList();
                notes.Add($"unsupported for a value comparison: {string.Join(", ", names)} (check by eye or by a derived number)");
            }
            if (a.Truncated || b.Truncated) notes.Insert(0, "partial: the read stopped at the item cap, so only a prefix of the data was compared");

            var verdict = count > 0 ? "differs" : unsupported ? "unsupported" : "equal";
            return new OutputVerdict(a.Name, verdict, pathsA.Count, itemsA, itemsB, compared, count,
                notes.Count == 0 ? null : string.Join("; ", notes));
        }

        static void Record(List<OutputMismatch> sink, int max, ref int total, OutputMismatch m)
        {
            total++;
            if (sink.Count < max) sink.Add(m);
        }

        /// <summary>Null when the two items agree; a reason when they do not. <paramref name="judged"/>
        /// is false when either side is of a kind the comparison cannot value-check (the type names
        /// are still required to agree, and that disagreement is a reason).</summary>
        static string? CompareItem(ComparableItem a, ComparableItem b, double tolerance, out bool judged)
        {
            judged = true;
            if (a.Kind == ComparableKind.Unsupported || b.Kind == ComparableKind.Unsupported)
            {
                if (!string.Equals(a.TypeName, b.TypeName, StringComparison.Ordinal)) return $"type differs: {a.TypeName} vs {b.TypeName}";
                judged = false;
                return null;
            }

            var numericA = IsNumeric(a.Kind);
            var numericB = IsNumeric(b.Kind);
            if (numericA && numericB)
            {
                if (a.Numbers is null || b.Numbers is null) return "no numeric value to compare";
                if (a.Numbers.Count != b.Numbers.Count) return $"kind differs: {a.Kind} vs {b.Kind}";
                var exact = a.Kind == ComparableKind.Integer && b.Kind == ComparableKind.Integer;
                for (var i = 0; i < a.Numbers.Count; i++)
                {
                    var x = a.Numbers[i];
                    var y = b.Numbers[i];
                    if (double.IsNaN(x) && double.IsNaN(y)) continue;
                    var delta = Math.Abs(x - y);
                    if (exact ? delta != 0 : !(delta <= tolerance))
                        return a.Numbers.Count == 1
                            ? $"differs by {Fmt(delta)} (tolerance {Fmt(tolerance)})"
                            : $"coordinate {i} differs by {Fmt(delta)} (tolerance {Fmt(tolerance)})";
                }
                return null;
            }
            if (numericA != numericB) return $"kind differs: {a.Kind} vs {b.Kind}";

            if (a.Kind != b.Kind) return $"kind differs: {a.Kind} vs {b.Kind}";
            return string.Equals(a.Text ?? "", b.Text ?? "", StringComparison.Ordinal) ? null : "text differs";
        }

        static bool IsNumeric(ComparableKind kind) => kind switch
        {
            ComparableKind.Number or ComparableKind.Integer or ComparableKind.Point or ComparableKind.Vector
                or ComparableKind.Line or ComparableKind.Plane or ComparableKind.Interval => true,
            _ => false,
        };

        static string Kinds(ComparableOutput output)
            => string.Join("|", output.Branches.SelectMany(br => br.Items).Select(it => it.Kind.ToString()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal));

        static string Paths(IReadOnlyList<string> paths)
        {
            const int shown = 6;
            var head = string.Join(" ", paths.Take(shown));
            return paths.Count <= shown ? $"{paths.Count}: {head}" : $"{paths.Count}: {head} …";
        }

        static string Show(ComparableItem item)
        {
            if (item.Numbers is not null)
                return item.Numbers.Count == 1 ? Fmt(item.Numbers[0]) : "(" + string.Join(", ", item.Numbers.Select(Fmt)) + ")";
            var text = item.Text ?? item.TypeName;
            return text.Length <= MaxValueChars ? text : text.Substring(0, MaxValueChars) + "…";
        }

        static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        static string Summarize(IReadOnlyList<OutputVerdict> verdicts, bool allEqual, IReadOnlyList<string> manual, int partial)
        {
            if (verdicts.Count == 0) return "no outputs on either component";
            if (allEqual) return $"equivalent: {verdicts.Count} output(s) match in shape and value within tolerance";
            var parts = new List<string>();
            var differs = verdicts.Count(v => v.Verdict == "differs");
            var missing = verdicts.Count(v => v.Verdict.StartsWith("missing", StringComparison.Ordinal));
            if (differs > 0) parts.Add($"{differs} output(s) differ");
            if (missing > 0) parts.Add($"{missing} output(s) exist on one side only");
            if (manual.Count > 0) parts.Add($"{manual.Count} output(s) need a manual check ({string.Join(", ", manual)})");
            if (partial > 0) parts.Add($"{partial} output(s) compared only up to the item cap");
            return "not proven equivalent: " + string.Join("; ", parts);
        }
    }
}
