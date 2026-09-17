// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// What the wireify registry has to say about itself. A socket number is resolved to an id
    /// client-side — the agent matches "do #n" against this list — so two objects carrying one
    /// number is a hazard the server can only NAME, never refuse (round-12 S12.13: a pasted
    /// socket kept its source's number and the summary listed two `W2`s without a word).
    /// The pasted socket renumbers itself on its next solve; until then, and for any other
    /// duplicate, the summary says so where the agent looks first. Pure.
    /// </summary>
    public static class RegistryWarnings
    {
        public static IReadOnlyList<string>? DuplicateNumbers(IReadOnlyList<WireifyComponentInfo> registry)
        {
            if (registry is null) throw new ArgumentNullException(nameof(registry));
            var warnings = new List<string>();
            foreach (var group in registry.Where(w => w.Number > 0).GroupBy(w => w.Number).Where(g => g.Count() > 1).OrderBy(g => g.Key))
            {
                var ids = string.Join(", ", group.Select(w => $"{w.Id} ({w.State})"));
                warnings.Add(
                    $"number {group.Key} is carried by {group.Count()} objects — {ids}. 'do #{group.Key}' is ambiguous: " +
                    "a pasted socket renumbers itself on its next solve; otherwise rename one (rename_component) so exactly " +
                    $"one object reads W{group.Key}, and address the other by its id meanwhile.");
            }
            return warnings.Count == 0 ? null : warnings;
        }
    }
}
