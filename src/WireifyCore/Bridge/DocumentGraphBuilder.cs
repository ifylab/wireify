// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WireifyCore.Bridge
{
    /// <summary>One param's key and the live wire ends on it (sources on an input, recipients
    /// on an output) — the bridge's plain reading of a Grasshopper param for the graph. A
    /// floating param (panel, relay, slider) is ONE param on both sides: its own sources are
    /// its inputs, its recipients its outputs (round-12 S12.3: reading only components' inputs
    /// dropped every wire INTO a panel or a relay param, 19 of 198 on the truss, silently).</summary>
    public sealed record WiredParam(string Key, IReadOnlyList<WireEndInfo> Ends);

    /// <summary>One kept object's wiring, both directions.</summary>
    public sealed record GraphWiring(Guid Id, IReadOnlyList<WiredParam> Inputs, IReadOnlyList<WiredParam> Outputs);

    /// <summary>
    /// The pure half of get_document_graph: the edge list, the payload estimate, and the cut.
    /// Edges address the kept nodes by INDEX (document order) and carry a guid only for an
    /// endpoint outside the kept set — two 36-character ids per wire is what put a 156-node
    /// canvas past the tool-result limit in round 12 (S12.2/S12.4). Every wire touching a kept
    /// object appears exactly once: a wire between two kept objects is taken from the
    /// recipient's input side; a wire from a kept output to an object outside the kept set from
    /// the output side, so the far end is still addressable. Tests run without Rhino.
    /// </summary>
    public static class DocumentGraphBuilder
    {
        /// <summary>The estimated-token budget a whole-canvas graph stays under. Claude Code
        /// refused results of 54,000 and 76,000 characters on a 192-object canvas (round-12
        /// S12.2/S12.4), so the budget is counted in tokens over the WHOLE reply, not in
        /// characters over the nodes alone.</summary>
        public const int TokenBudget = 15_000;

        static readonly Regex GuidPattern = new(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

        /// <summary>A rough token count for a JSON payload: a guid tokenizes to roughly 22
        /// tokens, everything else to roughly one token per 3.3 characters. Conservative on
        /// purpose — the budget exists to keep a reply readable, not to fill it.</summary>
        public static int EstimateTokens(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            var guids = GuidPattern.Matches(json).Count;
            var rest = Math.Max(0, json.Length - guids * 36);
            return guids * 22 + (int)Math.Ceiling(rest / 3.3);
        }

        /// <summary>The truncation note: what was kept, how it was chosen, and the remedies
        /// that are still open — <c>includeParams: false</c> is offered only while params are
        /// on (round-13 S13.3: the note suggested the flag that was already in effect).</summary>
        public static string TruncationNote(int fit, int total, bool includeParams)
        {
            var note = $"payload budget (~{TokenBudget:N0} tokens): {fit} of {total} nodes kept, "
                + "selected and W-numbered first — read the rest with ids or nameFilter";
            return includeParams ? note + ", or pass includeParams: false for the wiring alone" : note;
        }

        /// <summary>How many of <paramref name="count"/> priority-ordered nodes fit
        /// <paramref name="budget"/>, given <paramref name="estimateFor"/>(k) = the estimated
        /// tokens of the whole reply built from the first k. Binary search — the estimate only
        /// grows with k — so a 300-node canvas costs about nine serializations.</summary>
        public static int FitWithinBudget(int count, int budget, Func<int, int> estimateFor)
        {
            if (estimateFor is null) throw new ArgumentNullException(nameof(estimateFor));
            if (count <= 0 || estimateFor(count) <= budget) return count;
            int lo = 0, hi = count;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (estimateFor(mid) <= budget) lo = mid; else hi = mid;
            }
            return lo;
        }

        public static IReadOnlyList<GraphEdge> Edges(IReadOnlyList<GraphWiring> nodes)
        {
            if (nodes is null) throw new ArgumentNullException(nameof(nodes));
            var index = new Dictionary<Guid, int>();
            for (var i = 0; i < nodes.Count; i++) index[nodes[i].Id] = i;
            var seen = new HashSet<string>();
            var edges = new List<GraphEdge>();

            void Add(Guid fromId, string fromParam, Guid toId, string toParam)
            {
                if (!seen.Add($"{fromId}|{fromParam}|{toId}|{toParam}")) return;
                var from = index.TryGetValue(fromId, out var fi) ? fi : -1;
                var to = index.TryGetValue(toId, out var ti) ? ti : -1;
                edges.Add(new GraphEdge(
                    from, fromParam, to, toParam,
                    from < 0 ? fromId : null,
                    to < 0 ? toId : null));
            }

            foreach (var node in nodes)
            {
                foreach (var input in node.Inputs)
                    foreach (var source in input.Ends)
                        Add(source.ComponentId, source.Param, node.Id, input.Key);
                foreach (var output in node.Outputs)
                    foreach (var recipient in output.Ends)
                        if (!index.ContainsKey(recipient.ComponentId))
                            Add(node.Id, output.Key, recipient.ComponentId, recipient.Param);
            }
            return edges;
        }
    }
}
