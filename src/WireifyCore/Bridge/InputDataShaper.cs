// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace WireifyCore.Bridge
{
    /// <summary>One item on a branch, already read out of the Grasshopper goo into strings.</summary>
    public sealed record ShapedItem(string TypeName, string Clr, string Value);

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
        public static InputData Shape(
            string param,
            string access,
            IReadOnlyList<ShapedBranch> branches,
            int maxPerBranch,
            int maxTotal)
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
                .Select(g => new TypeCount(g.Key, g.First().Clr, g.Count()))
                .ToList();

            var samples = new List<DataSample>();
            foreach (var branch in branches)
            {
                if (samples.Count >= maxTotal) break;
                var taken = 0;
                foreach (var item in branch.Items)
                {
                    if (taken >= maxPerBranch || samples.Count >= maxTotal) break;
                    samples.Add(new DataSample(branch.Path, item.Value, item.TypeName));
                    taken++;
                }
            }

            return new InputData(param, access, tree, types, samples);
        }
    }
}
