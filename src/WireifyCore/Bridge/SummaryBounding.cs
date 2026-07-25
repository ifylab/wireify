// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace WireifyCore.Bridge
{
    /// <summary>One document object as a bounding candidate: its transport ref plus the two
    /// priority signals (selection, Wireify membership) the cap keys on.</summary>
    public sealed record SummaryCandidate(ComponentRef Ref, bool Selected, bool IsWireify);

    /// <summary>
    /// Bounds get_document_summary's components list so one-call orientation stays viable on
    /// production-size canvases (thousands of objects): optional name filter, then a cap that
    /// prioritizes selected and Wireify-managed objects while keeping document order in the
    /// output. Pure — the bridge feeds candidates, tests run without Rhino. The wireify registry
    /// is never routed through here: it is small, load-bearing, and never truncated.
    /// </summary>
    public static class SummaryBounding
    {
        public const int DefaultMaxComponents = 300;

        public static (IReadOnlyList<ComponentRef> Components, bool Truncated) Apply(
            IReadOnlyList<SummaryCandidate> candidates, int maxComponents, string? nameFilter)
        {
            if (candidates is null) throw new ArgumentNullException(nameof(candidates));

            var indexed = candidates.Select((c, i) => (C: c, Index: i));
            if (!string.IsNullOrWhiteSpace(nameFilter))
                indexed = indexed.Where(x =>
                    x.C.Ref.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.C.Ref.NickName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = indexed.ToList();
            if (maxComponents <= 0 || list.Count <= maxComponents)
                return (list.Select(x => x.C.Ref).ToList(), false);

            var kept = list
                .OrderBy(x => x.C.Selected ? 0 : x.C.IsWireify ? 1 : 2)
                .ThenBy(x => x.Index)
                .Take(maxComponents)
                .OrderBy(x => x.Index) // present in document order regardless of priority class
                .Select(x => x.C.Ref)
                .ToList();
            return (kept, true);
        }
    }
}
