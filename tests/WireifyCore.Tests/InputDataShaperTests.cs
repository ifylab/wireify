// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class InputDataShaperTests
{
    static ShapedBranch Branch(string path, params (string type, string clr, string val)[] items)
        => new(path, items.Select(i => new ShapedItem(i.type, i.clr, i.val)).ToList());

    [Fact]
    public void Tree_stats_reflect_branches_and_items()
    {
        var input = InputDataShaper.Shape("x", "tree", new[]
        {
            Branch("{0;0}", ("Number", "System.Double", "1"), ("Number", "System.Double", "2")),
            Branch("{0;1}", ("Number", "System.Double", "3")),
        }, 5, 50);

        Assert.Equal(2, input.Tree.PathCount);
        Assert.Equal(3, input.Tree.DataCount);
        Assert.False(input.Tree.IsFlat);
    }

    [Fact]
    public void Single_branch_is_flat()
    {
        var input = InputDataShaper.Shape("x", "list", new[] { Branch("{0}", ("Number", "System.Double", "1")) }, 5, 50);
        Assert.True(input.Tree.IsFlat);
    }

    [Fact]
    public void Histogram_groups_by_type_with_counts_and_clr()
    {
        var input = InputDataShaper.Shape("x", "list", new[]
        {
            Branch("{0}",
                ("Number", "System.Double", "1"),
                ("Number", "System.Double", "2"),
                ("Text", "System.String", "hi")),
        }, 10, 50);

        var num = input.Types.Single(t => t.TypeName == "Number");
        var txt = input.Types.Single(t => t.TypeName == "Text");
        Assert.Equal(2, num.Count);
        Assert.Equal("System.Double", num.Clr);
        Assert.Equal(1, txt.Count);
    }

    [Fact]
    public void Samples_cap_per_branch()
    {
        var input = InputDataShaper.Shape("x", "list", new[]
        {
            Branch("{0}", ("N", "C", "1"), ("N", "C", "2"), ("N", "C", "3"), ("N", "C", "4")),
        }, maxPerBranch: 2, maxTotal: 50);

        Assert.Equal(2, input.Samples.Count);
        Assert.Equal("1", input.Samples[0].Value);
        Assert.Equal("2", input.Samples[1].Value);
    }

    [Fact]
    public void Samples_cap_total_but_tree_stats_count_everything()
    {
        var branches = Enumerable.Range(0, 10)
            .Select(i => Branch($"{{{i}}}", ("N", "C", "a"), ("N", "C", "b")))
            .ToList();

        var input = InputDataShaper.Shape("x", "tree", branches, maxPerBranch: 2, maxTotal: 5);

        Assert.Equal(5, input.Samples.Count);   // total cap wins
        Assert.Equal(10, input.Tree.PathCount); // but the histogram + tree see all of it
        Assert.Equal(20, input.Tree.DataCount);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        var input = InputDataShaper.Shape("x", "item", new List<ShapedBranch>(), 5, 50);

        Assert.Equal(0, input.Tree.PathCount);
        Assert.Empty(input.Types);
        Assert.Empty(input.Samples);
        Assert.True(input.Tree.IsFlat);
    }
}
